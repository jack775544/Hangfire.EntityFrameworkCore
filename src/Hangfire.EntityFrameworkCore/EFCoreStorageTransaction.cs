﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Hangfire.Annotations;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;

namespace Hangfire.EntityFrameworkCore
{
    using GetHashFieldsFunc = Func<HangfireContext, string, IEnumerable<string>>;
    using GetJobStateExists = Func<HangfireContext, long, bool>;
    using GetSetValuesFunc = Func<HangfireContext, string, IEnumerable<string>>;
    using GetListsFunc = Func<HangfireContext, string, IEnumerable<HangfireList>>;
    using GetListPositionsFunc = Func<HangfireContext, string, IEnumerable<int>>;
    using GetMaxListPositionFunc = Func<HangfireContext, string, int?>;
    using SetExistsFunc = Func<HangfireContext, string, string, bool>;

    internal sealed class EFCoreStorageTransaction : JobStorageTransaction
    {
        private static GetHashFieldsFunc GetHashFieldsFunc { get; } = EF.CompileQuery(
            (HangfireContext context, string key) =>
                from x in context.Set<HangfireHash>()
                where x.Key == key
                select x.Field);

        private static GetJobStateExists GetJobStateExists { get; } = EF.CompileQuery(
            (HangfireContext context, long id) =>
                context.Set<HangfireJobState>().Any(x => x.JobId == id));

        private static GetListPositionsFunc GetListPositionsFunc { get; } = EF.CompileQuery(
            (HangfireContext context, string key) =>
                from item in context.Set<HangfireList>()
                where item.Key == key
                select item.Position);

        private static GetListsFunc GetListsFunc { get; } = EF.CompileQuery(
            (HangfireContext context, string key) =>
                from item in context.Set<HangfireList>()
                where item.Key == key
                select item);

        private static GetMaxListPositionFunc GetMaxListPositionFunc { get; } = EF.CompileQuery(
            (HangfireContext context, string key) =>
                context.Set<HangfireList>().
                    Where(x => x.Key == key).
                    Max(x => (int?)x.Position));

        private static GetSetValuesFunc GetSetValuesFunc { get; } = EF.CompileQuery(
            (HangfireContext context, string key) =>
                from set in context.Set<HangfireSet>()
                where set.Key == key
                select set.Value);

        private static SetExistsFunc SetExistsFunc { get; } = EF.CompileQuery(
            (HangfireContext context, string key, string value) =>
                context.Set<HangfireSet>().Any(x => x.Key == key && x.Value == value));

        private readonly EFCoreStorage _storage;
        private readonly Queue<Action<HangfireContext>> _queue;
        private readonly Queue<Action> _afterCommitQueue;
        private bool _disposed;

        public EFCoreStorageTransaction(
            EFCoreStorage storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _queue = new Queue<Action<HangfireContext>>();
            _afterCommitQueue = new Queue<Action>();
        }

        public override void AddJobState([NotNull] string jobId, [NotNull] IState state)
        {
            AddJobState(jobId, state, false);
        }

        public override void AddRangeToSet([NotNull] string key, [NotNull] IList<string> items)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            if (items == null)
                throw new ArgumentNullException(nameof(items));
            ThrowIfDisposed();

            var values = new HashSet<string>(items);

            _queue.Enqueue(context =>
            {
                var entries = context.ChangeTracker.Entries<HangfireSet>().
                    Where(x => x.Entity.Key == key && values.Contains(x.Entity.Value)).
                    ToDictionary(x => x.Entity.Value);
                var exisitingValues = new HashSet<string>(GetSetValuesFunc(context, key));

                foreach (var value in values)
                {
                    if (!entries.TryGetValue(value, out var entry))
                    {
                        if (!exisitingValues.Contains(value))
                            context.Add(new HangfireSet
                            {
                                Key = key,
                                Value = value,
                            });
                    }
                    else if (exisitingValues.Contains(value))
                        entry.State = EntityState.Unchanged;
                    else
                    {
                        entry.Entity.Score = 0;
                        entry.State = EntityState.Added;
                    }
                }
            });
        }

        public override void AddToQueue([NotNull] string queue, [NotNull] string jobId)
        {
            ValidateQueue(queue);
            long id = ValidateJobId(jobId);
            ThrowIfDisposed();

            var provider = _storage.GetQueueProvider(queue);
            var persistentQueue = provider.GetJobQueue();
            if (persistentQueue is EFCoreJobQueue storageJobQueue)
                _queue.Enqueue(context => storageJobQueue.Enqueue(context, queue, id));
            else
                _queue.Enqueue(context => persistentQueue.Enqueue(queue, jobId));
            if (persistentQueue is EFCoreJobQueue)
                _afterCommitQueue.Enqueue(
                    () => EFCoreJobQueue.NewItemInQueueEvent.Set());
        }

        public override void AddToSet([NotNull] string key, [NotNull] string value)
        {
            AddToSet(key, value, 0d);
        }

        public override void AddToSet([NotNull] string key, [NotNull] string value, double score)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            ThrowIfDisposed();

            _queue.Enqueue(context =>
            {
                var entry = context.ChangeTracker.
                    Entries<HangfireSet>().
                    FirstOrDefault(x =>
                        x.Entity.Key == key &&
                        x.Entity.Value == value);

                decimal scoreValue = (decimal)score;

                if (entry != null)
                {
                    var entity = entry.Entity;
                    entity.Score = scoreValue;
                    entity.CreatedAt = DateTime.UtcNow;
                    entry.State = EntityState.Modified;
                }
                else
                {
                    var set = new HangfireSet
                    {
                        CreatedAt = DateTime.UtcNow,
                        Key = key,
                        Score = scoreValue,
                        Value = value,
                    };

                    if (SetExistsFunc(context, key, value))
                        context.Update(set);
                    else
                        context.Add(set);
                }
            });
        }

        public override void Commit()
        {
            ThrowIfDisposed();

            _storage.UseContextSavingChanges(context =>
            {
                while (_queue.Count > 0)
                {
                    var action = _queue.Dequeue();
                    action.Invoke(context);
                }
                context.SaveChanges();
            });

            while (_afterCommitQueue.Count > 0)
            {
                var action = _afterCommitQueue.Dequeue();
                action.Invoke();
            }
        }

        public override void DecrementCounter([NotNull] string key)
        {
            AddCounter(key, -1L, default);
        }

        public override void DecrementCounter([NotNull] string key, TimeSpan expireIn)
        {
            AddCounter(key, -1L, DateTime.UtcNow + expireIn);
        }

        public override void Dispose()
        {
            base.Dispose();
            Dispose(true);
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _queue.Clear();
                    _afterCommitQueue.Clear();
                }
                _disposed = true;
            }
        }

        public override void ExpireJob([NotNull] string jobId, TimeSpan expireIn)
        {
            SetJobExpiration(jobId, DateTime.UtcNow + expireIn);
        }

        public override void ExpireHash([NotNull] string key, TimeSpan expireIn)
        {
            SetHashExpiration(key, DateTime.UtcNow + expireIn);
        }

        public override void ExpireList([NotNull] string key, TimeSpan expireIn)
        {
            SetListExpiration(key, DateTime.UtcNow + expireIn);
        }

        public override void ExpireSet([NotNull] string key, TimeSpan expireIn)
        {
            SetSetExpiration(key, DateTime.UtcNow + expireIn);
        }

        public override void IncrementCounter([NotNull] string key)
        {
            AddCounter(key, 1L, default);
        }

        public override void IncrementCounter([NotNull] string key, TimeSpan expireIn)
        {
            AddCounter(key, 1L, DateTime.UtcNow + expireIn);
        }

        public override void InsertToList([NotNull] string key, string value)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            ThrowIfDisposed();

            _queue.Enqueue(context =>
            {
                var maxPosition = context.ChangeTracker.
                    Entries<HangfireList>().
                    Where(x => x.Entity.Key == key).
                    Max(x => (int?)x.Entity.Position) ??
                    GetMaxListPositionFunc(context, key) ?? -1;

                context.Add(new HangfireList
                {
                    Key = key,
                    Position = maxPosition + 1,
                    Value = value,
                });
            });
        }

        public override void PersistJob([NotNull] string jobId)
        {
            SetJobExpiration(jobId, null);
        }

        public override void PersistHash([NotNull] string key)
        {
            SetHashExpiration(key, null);
        }

        public override void PersistList([NotNull] string key)
        {
            SetListExpiration(key, null);
        }

        public override void PersistSet([NotNull] string key)
        {
            SetSetExpiration(key, null);
        }

        public override void RemoveFromList([NotNull] string key, string value)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            ThrowIfDisposed();

            _queue.Enqueue(context =>
            {
                var list = GetListsFunc(context, key).OrderBy(x => x.Position).ToList();
                var newList = list. Where(x => x.Value != value).ToList();
                for (int i = newList.Count; i < list.Count; i++)
                    context.Remove(list[i]);
                CopyNonKeyValues(newList, list);
            });
        }

        public override void RemoveFromSet([NotNull] string key, [NotNull] string value)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            ThrowIfDisposed();

            _queue.Enqueue(context =>
            {
                var entry = context.ChangeTracker.
                    Entries<HangfireSet>().
                    SingleOrDefault(x => x.Entity.Key == key && x.Entity.Value == value);

                if (SetExistsFunc(context, key, value))
                {
                    if (entry == null)
                        entry = context.Attach(new HangfireSet
                        {
                            Key = key,
                            Value = value,
                        });
                    entry.State = EntityState.Deleted;
                }
                else if (entry != null && entry.State != EntityState.Detached)
                    entry.State = EntityState.Detached;
            });
        }

        public override void RemoveHash([NotNull] string key)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            ThrowIfDisposed();

            _queue.Enqueue(context =>
            {
                var entries = (
                    from entry in context.ChangeTracker.Entries<HangfireHash>()
                    where entry.Entity.Key == key
                    select entry).
                    ToDictionary(x => x.Entity.Field);

                var fields = GetHashFieldsFunc(context, key);

                foreach (var field in fields)
                    if (entries.TryGetValue(field, out var entry) &&
                        entry.State != EntityState.Deleted)
                        entry.State = EntityState.Deleted;
                    else
                        context.Remove(new HangfireHash
                        {
                            Key = key,
                            Field = field,
                        });
            });
        }

        public override void RemoveSet([NotNull] string key)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            ThrowIfDisposed();

            _queue.Enqueue(context =>
            {
                var entries = context.ChangeTracker.
                    Entries<HangfireSet>().
                    Where(x => x.Entity.Key == key).
                    ToDictionary(x => x.Entity.Value);

                var values = GetSetValuesFunc(context, key);

                foreach (var value in values)
                    if (entries.TryGetValue(value, out var entry) &&
                        entry.State != EntityState.Deleted)
                        entry.State = EntityState.Deleted;
                    else
                        context.Remove(new HangfireSet
                        {
                            Key = key,
                            Value = value,
                        });
            });
        }

        public override void SetJobState([NotNull] string jobId, [NotNull] IState state)
        {
            AddJobState(jobId, state, true);
        }

        public override void SetRangeInHash(
            [NotNull] string key,
            [NotNull] IEnumerable<KeyValuePair<string, string>> keyValuePairs)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            if (keyValuePairs == null)
                throw new ArgumentNullException(nameof(keyValuePairs));
            ThrowIfDisposed();

            _queue.Enqueue(context =>
            {
                var actualFields = new HashSet<string>(keyValuePairs.Select(x => x.Key));
                var exisitingFields = new HashSet<string>(GetHashFieldsFunc(context, key));
                var entries = (
                    from entry in context.ChangeTracker.Entries<HangfireHash>()
                    let entity = entry.Entity
                    where entity.Key == key && actualFields.Contains(entity.Field)
                    select entry).
                    ToDictionary(x => x.Entity.Field);

                foreach (var item in keyValuePairs)
                {
                    var field = item.Key;
                    if (!entries.TryGetValue(field, out var entry))
                    {
                        var hash = new HangfireHash
                        {
                            Key = key,
                            Field = field,
                        };
                        if (exisitingFields.Contains(field))
                            entry = context.Attach(hash);
                        else
                            entry = context.Add(hash);
                    }
                    entry.Entity.Value = item.Value;
                }
            });
        }

        public override void TrimList([NotNull] string key, int keepStartingFrom, int keepEndingAt)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            ThrowIfDisposed();

            _queue.Enqueue(context =>
            {
                var list = GetListsFunc(context, key).OrderBy(x => x.Position).ToList();
                var newList = list.
                    Where((item, index) => keepStartingFrom <= index && index <= keepEndingAt).
                    ToList();

                for (int i = newList.Count; i < list.Count; i++)
                    context.Remove(list[i]);

                CopyNonKeyValues(newList, list);
            });
        }

        private void AddCounter([NotNull] string key, long value, DateTime? expireAt)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            ThrowIfDisposed();

            _queue.Enqueue(context =>
            {
                var entity = (context.ChangeTracker.
                    Entries<HangfireCounter>().
                    FirstOrDefault(x => x.Entity.Key == key &&
                        (x.State == EntityState.Added || x.State == EntityState.Modified)) ??
                    context.Add(new HangfireCounter
                    {
                        Key = key,
                    })).
                    Entity;

                entity.Value = entity.Value + value;
                entity.ExpireAt = expireAt;
            });
        }

        private void AddJobState([NotNull] string jobId, [NotNull] IState state, bool setActual)
        {
            var id = ValidateJobId(jobId);
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            ThrowIfDisposed();

            _queue.Enqueue(context =>
            {
                var stateEntity = context.Add(new HangfireState
                {
                    JobId = id,
                    CreatedAt = DateTime.UtcNow,
                    Name = state.Name,
                    Reason = state.Reason,
                    Data = state.SerializeData(),
                }).Entity;

                if (setActual)
                {
                    var jobStateEntry = context.ChangeTracker.
                        Entries<HangfireJobState>().
                        SingleOrDefault(x => x.Entity.JobId == id) ??
                        context.Attach(new HangfireJobState
                        {
                            JobId = id,
                            Name = state.Name,
                        });
                    jobStateEntry.Entity.State = stateEntity;
                    jobStateEntry.State =
                        GetJobStateExists(context, id) ?
                        EntityState.Modified :
                        EntityState.Added;
                }
            });
        }

        private static void CopyNonKeyValues(
            IReadOnlyList<HangfireList> source,
            IReadOnlyList<HangfireList> destination)
        {
            var count = source.Count;
            for (int i = 0; i < count; i++)
            {
                var oldItem = destination[i];
                var newItem = source[i];
                if (ReferenceEquals(oldItem, newItem))
                    continue;
                oldItem.ExpireAt = newItem.ExpireAt;
                oldItem.Value = newItem.Value;
            }
        }

        private void SetJobExpiration(string jobId, DateTime? expireAt)
        {
            var id = ValidateJobId(jobId);
            ThrowIfDisposed();

            _queue.Enqueue(context =>
            {
                var entry = context.ChangeTracker.
                    Entries<HangfireJob>().
                    FirstOrDefault(x => x.Entity.Id == id);

                if (entry != null)
                    entry.Entity.ExpireAt = expireAt;
                else
                {
                    entry = context.Attach(new HangfireJob
                    {
                        Id = id,
                        ExpireAt = expireAt,
                    });
                }

                entry.Property(x => x.ExpireAt).IsModified = true;
            });
        }

        private void SetHashExpiration(string key, DateTime? expireAt)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            ThrowIfDisposed();

            _queue.Enqueue(context =>
            {
                var fields = new HashSet<string>(GetHashFieldsFunc(context, key));
                var entries = (
                    from entry in context.ChangeTracker.Entries<HangfireHash>()
                    let entity = entry.Entity
                    where entity.Key == key && fields.Contains(entity.Field)
                    select entry).
                    ToDictionary(x => x.Entity.Field);

                foreach (var field in fields)
                {
                    if (!entries.TryGetValue(field, out var entry))
                        entry = context.Attach(new HangfireHash
                        {
                            Key = key,
                            Field = field,
                        });

                    entry.Entity.ExpireAt = expireAt;
                    entry.Property(x => x.ExpireAt).IsModified = true;
                }
            });
        }

        private void SetListExpiration(string key, DateTime? expireAt)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            ThrowIfDisposed();

            _queue.Enqueue(context =>
            {
                var positions = new HashSet<int>(GetListPositionsFunc(context, key));
                var entries = (
                    from entry in context.ChangeTracker.Entries<HangfireList>()
                    let entity = entry.Entity
                    where entity.Key == key && positions.Contains(entity.Position)
                    select entry).
                    ToDictionary(x => x.Entity.Position);

                foreach (var position in positions)
                {
                    if (!entries.TryGetValue(position, out var entry))
                        entry = context.Attach(new HangfireList
                        {
                            Key = key,
                            Position = position,
                        });

                    entry.Entity.ExpireAt = expireAt;
                    entry.Property(x => x.ExpireAt).IsModified = true;
                }
            });
        }

        private void SetSetExpiration(string key, DateTime? expireAt)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            ThrowIfDisposed();

            _queue.Enqueue(context =>
            {
                var values = new HashSet<string>(GetSetValuesFunc(context, key));
                var entries = (
                    from entry in context.ChangeTracker.Entries<HangfireSet>()
                    let entity = entry.Entity
                    where entity.Key == key && values.Contains(entity.Value)
                    select entry).
                    ToDictionary(x => x.Entity.Value);

                foreach (var value in values)
                {
                    if (!entries.TryGetValue(value, out var entry))
                        entry = context.Attach(new HangfireSet
                        {
                            Key = key,
                            Value = value,
                        });

                    entry.Entity.ExpireAt = expireAt;
                    entry.Property(x => x.ExpireAt).IsModified = true;
                }
            });
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().FullName);
        }

        private static void ValidateQueue(string queue)
        {
            if (queue == null)
                throw new ArgumentNullException(nameof(queue));

            if (queue.Length == 0)
                throw new ArgumentException(null, nameof(queue));
        }

        private static long ValidateJobId(string jobId)
        {
            if (jobId == null)
                throw new ArgumentNullException(nameof(jobId));

            if (jobId.Length == 0)
                throw new ArgumentException(null, nameof(jobId));

            return long.Parse(jobId, CultureInfo.InvariantCulture);
        }
    }
}
