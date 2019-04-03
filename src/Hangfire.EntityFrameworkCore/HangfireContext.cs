﻿using System;
using Hangfire.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Hangfire.EntityFrameworkCore
{
    internal class HangfireContext : DbContext
    {
        internal string Schema { get; }

        public HangfireContext([NotNull] DbContextOptions options, [NotNull] string schema)
            : base(options)
        {
            Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.ReplaceService<IModelCacheKeyFactory, HangfireModelCacheKeyFactory>();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            if (!string.IsNullOrEmpty(Schema))
                modelBuilder.HasDefaultSchema(Schema);

            modelBuilder.Entity<HangfireCounter>(entity =>
            {
                entity.HasIndex(x => new { x.Key, x.Value });
                entity.HasIndex(x => x.ExpireAt);
            });

            modelBuilder.Entity<HangfireHash>(entity =>
            {
                entity.HasKey(x => new { x.Key, x.Field });
                entity.HasIndex(x => x.ExpireAt);
            });

            modelBuilder.Entity<HangfireJob>(entity =>
            {
                entity.HasIndex(x => x.StateName);
                entity.HasIndex(x => x.ExpireAt);
            });

            modelBuilder.Entity<HangfireJobParameter>(entity =>
            {
                entity.HasKey(x => new { x.JobId, x.Name });
            });

            modelBuilder.Entity<HangfireList>(entity =>
            {
                entity.HasKey(x => new { x.Key, x.Position });
                entity.HasIndex(x => x.ExpireAt);
            });

            modelBuilder.Entity<HangfireLock>();

            modelBuilder.Entity<HangfireQueuedJob>(entity =>
            {
                entity.HasIndex(x => new { x.Queue, x.FetchedAt });
            });

            modelBuilder.Entity<HangfireSet>(entity =>
            {
                entity.HasKey(x => new { x.Key, x.Value });
                entity.HasIndex(x => new { x.Key, x.Score });
                entity.HasIndex(x => x.ExpireAt);
            });

            modelBuilder.Entity<HangfireServer>(entity =>
            {
                entity.HasIndex(x => x.Heartbeat);
            });

            modelBuilder.Entity<HangfireState>(entity =>
            {
                entity.HasIndex(x => x.JobId);
                entity.HasMany<HangfireJob>().
                    WithOne(x => x.State).
                    HasForeignKey(x => x.StateId);
            });
        }
    }
}
