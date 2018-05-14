﻿using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace OrleansDashboard
{
    public class SiloGrain : Grain, ISiloGrain
    {
        const int DefaultTimerIntervalMs = 1000; // 1 second
        private readonly Queue<SiloRuntimeStatistics> stats = new Queue<SiloRuntimeStatistics>();
        private readonly Dictionary<string, StatCounter> counters = new Dictionary<string, StatCounter>();
        private IDisposable timer;
        private string versionOrleans;
        private string versionHost;
        private DashboardOptions options;

        public SiloGrain(IOptions<DashboardOptions> options)
        {
            this.options = options.Value;
        }

        public override async Task OnActivateAsync()
        {
            foreach (var x in Enumerable.Range(1, Dashboard.HistoryLength))
            {
                stats.Enqueue(null);
            }
            var updateInterval =  TimeSpan.FromMilliseconds(Math.Max(options.CounterUpdateIntervalMs, DefaultTimerIntervalMs));
            
            try
            {
                timer = RegisterTimer(x => CollectStatistics((bool)x), true, updateInterval, updateInterval);
                await CollectStatistics(false);
            }
            catch (InvalidOperationException)
            {
                Debug.WriteLine("Not running in Orleans runtime");
            }

            await base.OnActivateAsync();
        }

        private async Task CollectStatistics(bool canDeactivate)
        {
            var siloAddress = SiloAddress.FromParsableString(this.GetPrimaryKeyString());
            var managementGrain = GrainFactory.GetGrain<IManagementGrain>(0);
            try
            {
                var results = (await managementGrain.GetRuntimeStatistics(new SiloAddress[] { siloAddress })).FirstOrDefault();

                stats.Enqueue(results);

                while (stats.Count > Dashboard.HistoryLength)
                {
                    stats.Dequeue();
                }
            }
            catch (Exception)
            {
                // we can't get the silo stats, it's probably dead, so kill the grain
                if (canDeactivate)
                {
                    timer?.Dispose();
                    timer = null;

                    DeactivateOnIdle();
                }
            }
        }

        public Task<SiloRuntimeStatistics[]> GetRuntimeStatistics()
        {
            return Task.FromResult(stats.ToArray());
        }

        public Task SetVersion(string orleans, string host)
        {
            versionOrleans = orleans;
            versionHost = host;

            return Task.CompletedTask;
        }

        public Task<Dictionary<string, string>> GetExtendedProperties()
        {
            var results = new Dictionary<string, string>
            {
                ["HostVersion"] = versionHost,
                ["OrleansVersion"] = versionOrleans
            };

            return Task.FromResult(results);
        }

        public Task ReportCounters(StatCounter[] counters)
        {
            foreach (var counter in counters)
            {
                this.counters[counter.Name] = counter;
            }

            return Task.CompletedTask;
        }

        public Task<StatCounter[]> GetCounters()
        {
            return Task.FromResult(counters.Values.OrderBy(x => x.Name).ToArray());
        }
    }
}
