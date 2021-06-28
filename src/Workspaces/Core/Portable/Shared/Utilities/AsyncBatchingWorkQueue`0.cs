﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Shared.TestHooks;

namespace Roslyn.Utilities
{
    /// <inheritdoc cref="AsyncBatchingWorkQueue{TItem, TResult}"/>
    internal class AsyncBatchingWorkQueue : AsyncBatchingWorkQueue<bool>
    {
        public AsyncBatchingWorkQueue(
            TimeSpan delay,
            Func<CancellationToken, Task> processBatchAsync,
            CancellationToken cancellationToken)
            : this(delay,
                   processBatchAsync,
                   asyncListener: null,
                   cancellationToken)
        {
        }

        public AsyncBatchingWorkQueue(
            TimeSpan delay,
            Func<CancellationToken, Task> processBatchAsync,
            IAsynchronousOperationListener? asyncListener,
            CancellationToken cancellationToken)
            : base(delay, Convert(processBatchAsync), EqualityComparer<bool>.Default, asyncListener, cancellationToken)
        {
        }

        private static Func<ImmutableArray<bool>, CancellationToken, Task> Convert(Func<CancellationToken, Task> processBatchAsync)
            => (items, ct) => processBatchAsync(ct);

        public void AddWork()
            => base.AddWork(true);
    }
}
