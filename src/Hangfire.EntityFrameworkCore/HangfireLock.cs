﻿using System;
using System.ComponentModel.DataAnnotations;

namespace Hangfire.EntityFrameworkCore
{
    internal class HangfireLock
    {
        [StringLength(100)]
        public string Id { get; set; }

        public DateTime AcquiredAt { get; set; }
    }
}
