﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// A thread-safe, lazily and asynchronously evaluated value factory.
/// </summary>
/// <typeparam name="T">The type of value generated by the value factory.</typeparam>
/// <remarks>
/// This class does not itself carry any resources needful of disposing.
/// But the value factory may produce a value that needs to be disposed of,
/// which is why this class carries a <see cref="DisposeValueAsync"/> method but does not implement <see cref="IDisposable"/>.
/// </remarks>
public class AsyncLazy<T>
{
    /// <summary>
    /// The value set to the <see cref="recursiveFactoryCheck"/> field
    /// while the value factory is executing.
    /// </summary>
    private static readonly object RecursiveCheckSentinel = new object();

    /// <summary>
    /// A value set on the <see cref="value"/> field when this object is disposed.
    /// </summary>
    private static readonly Task<T> DisposedSentinel = Task.FromException<T>(new ObjectDisposedException(nameof(AsyncLazy<T>)));

    /// <summary>
    /// The object to lock to provide thread-safety.
    /// </summary>
    private readonly object syncObject = new object();

    /// <summary>
    /// The unique instance identifier.
    /// </summary>
    private readonly AsyncLocal<object> recursiveFactoryCheck = new AsyncLocal<object>();

    /// <summary>
    /// An optional means to avoid deadlocks when synchronous APIs are called that must invoke async methods in user code.
    /// </summary>
    private readonly JoinableTaskFactory? jobFactory;

    /// <summary>
    /// The function to invoke to produce the task.
    /// </summary>
    private Func<Task<T>>? valueFactory;

    /// <summary>
    /// The result of the value factory.
    /// </summary>
    private Task<T>? value;

    /// <summary>
    /// A joinable task whose result is the value to be cached.
    /// </summary>
    private JoinableTask<T>? joinableTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncLazy{T}"/> class.
    /// </summary>
    /// <param name="valueFactory">The async function that produces the value.  To be invoked at most once.</param>
    /// <param name="joinableTaskFactory">
    /// The <see cref="JoinableTaskFactory" /> to use for avoiding deadlocks when the <paramref name="valueFactory"/>
    /// or the constructed value's <see cref="System.IAsyncDisposable.DisposeAsync"/> method may require the main thread in the process.
    /// </param>
    public AsyncLazy(Func<Task<T>> valueFactory, JoinableTaskFactory? joinableTaskFactory = null)
    {
        Requires.NotNull(valueFactory, nameof(valueFactory));
        this.valueFactory = valueFactory;
        this.jobFactory = joinableTaskFactory;
    }

    /// <summary>
    /// Gets a value indicating whether the value factory has been invoked.
    /// </summary>
    /// <remarks>
    /// This returns <see langword="false" /> after a call to <see cref="DisposeValue"/>.
    /// </remarks>
    public bool IsValueCreated
    {
        get
        {
            // This is carefully written to interact well with the DisposeValueAsync method
            // without requiring a lock here.
            bool result = Volatile.Read(ref this.valueFactory) is null;
            Interlocked.MemoryBarrier();
            result &= Volatile.Read(ref this.value) != DisposedSentinel;
            return result;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the value factory has been invoked and has run to completion.
    /// </summary>
    /// <remarks>
    /// This returns <see langword="false" /> after a call to <see cref="DisposeValue"/>.
    /// </remarks>
    public bool IsValueFactoryCompleted
    {
        get
        {
            Task<T>? value = Volatile.Read(ref this.value);
            return value is object && value.IsCompleted && value != DisposedSentinel;
        }
    }

    /// <summary>
    /// Gets a value indicating whether <see cref="DisposeValue"/> has already been called.
    /// </summary>
    public bool IsValueDisposed => Volatile.Read(ref this.value) == DisposedSentinel;

    /// <summary>
    /// Gets the task that produces or has produced the value.
    /// </summary>
    /// <returns>A task whose result is the lazily constructed value.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the value factory calls <see cref="GetValueAsync()"/> on this instance.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown after <see cref="DisposeValue"/> is called.</exception>
    public Task<T> GetValueAsync() => this.GetValueAsync(CancellationToken.None);

    /// <summary>
    /// Gets the task that produces or has produced the value.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token whose cancellation indicates that the caller no longer is interested in the result.
    /// Note that this will not cancel the value factory (since other callers may exist).
    /// But this token will result in an expediant cancellation of the returned Task,
    /// and a dis-joining of any <see cref="JoinableTask"/> that may have occurred as a result of this call.
    /// </param>
    /// <returns>A task whose result is the lazily constructed value.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the value factory calls <see cref="GetValueAsync()"/> on this instance.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown after <see cref="DisposeValue"/> is called.</exception>
    public Task<T> GetValueAsync(CancellationToken cancellationToken)
    {
        if (!((this.value is object && this.value.IsCompleted) || this.recursiveFactoryCheck.Value is null))
        {
            // PERF: we check the condition and *then* retrieve the string resource only on failure
            // because the string retrieval has shown up as significant on ETL traces.
            Verify.FailOperation(Strings.ValueFactoryReentrancy);
        }

        if (this.value is null)
        {
            if (Monitor.IsEntered(this.syncObject))
            {
                // PERF: we check the condition and *then* retrieve the string resource only on failure
                // because the string retrieval has shown up as significant on ETL traces.
                Verify.FailOperation(Strings.ValueFactoryReentrancy);
            }

            InlineResumable? resumableAwaiter = null;
            lock (this.syncObject)
            {
                // Note that if multiple threads hit GetValueAsync() before
                // the valueFactory has completed its synchronous execution,
                // then only one thread will execute the valueFactory while the
                // other threads synchronously block till the synchronous portion
                // has completed.
                if (this.value is null)
                {
                    RoslynDebug.Assert(this.valueFactory is object);

                    cancellationToken.ThrowIfCancellationRequested();
                    resumableAwaiter = new InlineResumable();
                    Func<Task<T>>? originalValueFactory = this.valueFactory;
                    this.valueFactory = null;
                    Func<Task<T>> valueFactory = async delegate
                    {
                        try
                        {
                            await resumableAwaiter;
                            return await originalValueFactory().ConfigureAwaitRunInline();
                        }
                        finally
                        {
                            this.joinableTask = null;
                        }
                    };

                    this.recursiveFactoryCheck.Value = RecursiveCheckSentinel;
                    try
                    {
                        if (this.jobFactory is object)
                        {
                            // Wrapping with RunAsync allows a future caller
                            // to synchronously block the Main thread waiting for the result
                            // without leading to deadlocks.
                            this.joinableTask = this.jobFactory.RunAsync(valueFactory);
                            this.value = this.joinableTask.Task;
                        }
                        else
                        {
                            this.value = valueFactory();
                        }
                    }
                    finally
                    {
                        this.recursiveFactoryCheck.Value = null;
                    }
                }
            }

            // Allow the original value factory to actually run.
            resumableAwaiter?.Resume();
        }

        if (!this.value.IsCompleted)
        {
            this.joinableTask?.JoinAsync(cancellationToken).Forget();
        }

        return this.value.WithCancellation(cancellationToken);
    }

    /// <summary>
    /// Gets the lazily computed value.
    /// </summary>
    /// <returns>The lazily constructed value.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the value factory calls <see cref="GetValueAsync()"/> on this instance.
    /// </exception>
    public T GetValue() => this.GetValue(CancellationToken.None);

    /// <summary>
    /// Gets the lazily computed value.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token whose cancellation indicates that the caller no longer is interested in the result.
    /// Note that this will not cancel the value factory (since other callers may exist).
    /// But when this token is canceled, the caller will experience an <see cref="OperationCanceledException"/>
    /// immediately and a dis-joining of any <see cref="JoinableTask"/> that may have occurred as a result of this call.
    /// </param>
    /// <returns>The lazily constructed value.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the value factory calls <see cref="GetValueAsync()"/> on this instance.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled before the value is computed.</exception>
    public T GetValue(CancellationToken cancellationToken)
    {
        // As a perf optimization, avoid calling JTF or GetValueAsync if the value factory has already completed.
        if (this.IsValueFactoryCompleted)
        {
            RoslynDebug.Assert(this.value is object);

            return this.value.GetAwaiter().GetResult();
        }
        else
        {
            return this.jobFactory is JoinableTaskFactory jtf
                ? jtf.Run(() => this.GetValueAsync(cancellationToken))
                : this.GetValueAsync(cancellationToken).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Disposes of the lazily-initialized value if disposable, and causes all subsequent attempts to obtain the value to fail.
    /// </summary>
    /// <remarks>
    /// <para>This call will block on disposal (which may include construction of the value itself if it has already started but not yet finished) if it is the first call to dispose of the value.</para>
    /// <para>Calling this method will put this object into a disposed state where future calls to obtain the value will throw <see cref="ObjectDisposedException"/>.</para>
    /// <para>If the value has already been produced and implements <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/>, it will be disposed of.
    /// If the value factory has already started but has not yet completed, its value will be disposed of when the value factory completes.</para>
    /// <para>If prior calls to obtain the value are in flight when this method is called, those calls <em>may</em> complete and their callers may obtain the value, although <see cref="IDisposable.Dispose"/>
    /// may have been or will soon be called on the value, leading those users to experience a <see cref="ObjectDisposedException"/>.</para>
    /// <para>Note all conditions based on the value implementing <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/> is based on the actual value, rather than the <typeparamref name="T"/> type argument.
    /// This means that although <typeparamref name="T"/> may be <c>IFoo</c> (which does not implement <see cref="IDisposable"/>), the concrete type that implements <c>IFoo</c> may implement <see cref="IDisposable"/>
    /// and thus be treated as a disposable object as described above.</para>
    /// </remarks>
    public void DisposeValue()
    {
        if (!this.IsValueDisposed)
        {
            if (this.jobFactory is JoinableTaskFactory jtf)
            {
                jtf.Run(this.DisposeValueAsync);
            }
            else
            {
                this.DisposeValueAsync().GetAwaiter().GetResult();
            }
        }
    }

    /// <summary>
    /// Disposes of the lazily-initialized value if disposable, and causes all subsequent attempts to obtain the value to fail.
    /// </summary>
    /// <returns>
    /// A task that completes when the value has been disposed of, or immediately if the value has already been disposed of or has been scheduled for disposal by a prior call.
    /// </returns>
    /// <remarks>
    /// <para>Calling this method will put this object into a disposed state where future calls to obtain the value will throw <see cref="ObjectDisposedException"/>.</para>
    /// <para>If the value has already been produced and implements <see cref="IDisposable"/>, <see cref="IAsyncDisposable"/>,  or <see cref="System.IAsyncDisposable"/> it will be disposed of.
    /// If the value factory has already started but has not yet completed, its value will be disposed of when the value factory completes.</para>
    /// <para>If prior calls to obtain the value are in flight when this method is called, those calls <em>may</em> complete and their callers may obtain the value, although <see cref="IDisposable.Dispose"/>
    /// may have been or will soon be called on the value, leading those users to experience a <see cref="ObjectDisposedException"/>.</para>
    /// <para>Note all conditions based on the value implementing <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/> is based on the actual value, rather than the <typeparamref name="T"/> type argument.
    /// This means that although <typeparamref name="T"/> may be <c>IFoo</c> (which does not implement <see cref="IDisposable"/>), the concrete type that implements <c>IFoo</c> may implement <see cref="IDisposable"/>
    /// and thus be treated as a disposable object as described above.</para>
    /// </remarks>
    public async Task DisposeValueAsync()
    {
        Task<T>? localValueTask = null;
        object? localValue = default;
        lock (this.syncObject)
        {
            if (this.value == DisposedSentinel)
            {
                return;
            }

            switch (this.value?.Status)
            {
                case TaskStatus.RanToCompletion:
                    // We'll dispose of the value inline, outside the lock.
                    localValue = this.value.Result;
                    break;
                case TaskStatus.Faulted:
                case TaskStatus.Canceled:
                    // Nothing left to do.
                    break;
                default:
                    // We'll schedule the value for disposal outside the lock so it can be synchronous with the value factory,
                    // but will not execute within our lock.
                    localValueTask = this.value;
                    break;
            }

            // Shut out all future callers from obtaining the value.
            this.value = DisposedSentinel;

            // We want value to be set before valueFactory is cleared so that IsValueCreated never returns true incorrectly.
            Interlocked.MemoryBarrier();

            // Release associated memory.
            this.joinableTask = null;
            this.valueFactory = null;
        }

        if (localValueTask is not null)
        {
            localValue = await localValueTask.ConfigureAwait(false);
        }

        if (localValue is System.IAsyncDisposable systemAsyncDisposable)
        {
            await systemAsyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (localValue is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (localValue is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// Renders a string describing an uncreated value, or the string representation of the created value.
    /// </summary>
    public override string ToString()
    {
        return (this.value is object && this.value.IsCompleted)
            ? (this.value.Status == TaskStatus.RanToCompletion ? $"{this.value.Result}" : Strings.LazyValueFaulted)
            : Strings.LazyValueNotCreated;
    }
}
