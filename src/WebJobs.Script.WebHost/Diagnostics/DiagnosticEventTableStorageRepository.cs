﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Timers;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Azure.WebJobs.Script.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Timer = System.Timers.Timer;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Diagnostics
{
    public class DiagnosticEventTableStorageRepository : IDiagnosticEventRepository
    {
        private const int MaximumTableNameLength = 63;
        private const string TableNamePreFix = "MSDiagnosticEvents";
        private readonly ConcurrentDictionary<string, DiagnosticEvent> _events = new ConcurrentDictionary<string, DiagnosticEvent>();
        private readonly Timer _resetTimer;
        private IConfiguration _configuration;
        private IOptionsMonitor<AppServiceOptions> _appServiceOptions;
        private CloudTableClient _tableClient;
        private CloudTable _logTable;
        private ILogger _logger;

        public DiagnosticEventTableStorageRepository(IConfiguration configuration, IOptionsMonitor<AppServiceOptions> appServiceOptions, ILogger<DiagnosticEventTableStorageRepository> logger)
        {
            _configuration = configuration;
            _appServiceOptions = appServiceOptions;
            _resetTimer = new Timer()
            {
                AutoReset = true,
                Interval = 60 * 1000, // 10 mins
                Enabled = true
            };

            _resetTimer.Elapsed += OnFlushLogs;
            _logger = logger;
        }

        private CloudTableClient TableClient
        {
            get
            {
                if (_tableClient == null)
                {
                    string storageConnectionString = _configuration.GetWebJobsConnectionString(ConnectionStringNames.Storage);
                    if (!string.IsNullOrEmpty(storageConnectionString)
                        && CloudStorageAccount.TryParse(storageConnectionString, out CloudStorageAccount account))
                    {
                        var tableClientConfig = new TableClientConfiguration();
                        _tableClient = new CloudTableClient(account.TableStorageUri, account.Credentials, tableClientConfig);
                        Console.WriteLine("**** table client initialized");
                    }
                }
                return _tableClient;
            }
        }

        private CloudTable GetLogTable()
        {
            if (_logTable == null && TableClient != null)
            {
                string tableName = NormalizedTableName(_appServiceOptions.CurrentValue.AppName);
                CloudTable table = TableClient.GetTableReference(tableName);
                table.CreateIfNotExists();
                _logger.LogInformation("Diagnostic table name set to {tableName}", tableName);
                _logTable = table;
                Console.WriteLine("**** logtable client initialized");
                Console.WriteLine($"**** logtable name:{tableName}");
            }

            return _logTable;
        }

        private void OnFlushLogs(object sender, ElapsedEventArgs e)
        {
            FlushLogs();
        }

        public void FlushLogs()
        {
            Console.WriteLine("**** Flush logs called");
            try
            {
                var table = GetLogTable();
                foreach (string errorCode in _events.Keys)
                {
                    Console.WriteLine($"**** Inserting:{errorCode}, Hitcount:{_events[errorCode].HitCount}");
                    TableOperation insertOperation = TableOperation.Insert(_events[errorCode]);
                    TableResult result = table.Execute(insertOperation);
                    _events.Remove(errorCode, out DiagnosticEvent diagnosticEvent);
                }
            }
            catch (StorageException exeception)
            {
                _logger.LogError(exeception, "Error writing logs to table storage");
            }
        }

        public void AddDiagnosticEvent(DateTime timestamp, string errorCode, LogLevel level, string message, string helpLink, Exception exception)
        {
            var diagnosticEvent = new DiagnosticEvent()
            {
                ErrorCode = errorCode,
                HelpLink = helpLink,
                LastTimeStamp = timestamp,
                Message = message,
                Level = level,
                Details = exception?.ToFormattedString()
            };

            _events.AddOrUpdate(errorCode, diagnosticEvent, (e, a) =>
            {
                a.HitCount++;
                a.LastTimeStamp = timestamp;
                return a;
            });

            Console.WriteLine($"**** Recording:{diagnosticEvent.ErrorCode}, Hitcount:{diagnosticEvent.HitCount}");
        }

        private static string NormalizedTableName(string appName)
        {
            string alphanumericStr = Regex.Replace(appName, @"[^0-9A-Za-z ,]", string.Empty);
            string name = TableNamePreFix + alphanumericStr;
            int strLength = Math.Min(name.Length, MaximumTableNameLength);
            return name.Substring(0, strLength);
        }
    }
}
