﻿using Axinom.Toolkit;
using Docker.DotNet;
using Docker.DotNet.Models;
using Prometheus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DockerExporter
{
    /// <summary>
    /// Tracks the status of one instance of Docker and exports metrics, updating the data when new scrapes are requested.
    /// </summary>
    /// <remarks>
    /// Thread-safe.
    /// </remarks>
    sealed class DockerTracker
    {
        public Uri DockerUrl { get; }

        private readonly DockerClientConfiguration _clientConfiguration;

        // If an execution can get the lock on first try, it will really perform the update.
        // Otherwise, it will wait for the lock and then perform a no-op update to just leave
        // the tracker with the same data the just-finished update generated.
        // This acts as basic rate control.
        private readonly SemaphoreSlim _updateLock = new SemaphoreSlim(1);

        public DockerTracker(Uri dockerUrl)
        {
            DockerUrl = dockerUrl;

            // TODO: Support mutual authentication via certificates.
            _clientConfiguration = new DockerClientConfiguration(dockerUrl, null, Constants.DockerCommandTimeout);
        }

        /// <summary>
        /// Requests the tracker to update its data set.
        /// </summary>
        /// <remarks>
        /// May be called multiple times concurrently.
        /// 
        /// The method returns to signal that the trackerss of all containers
        /// when the method was called have attempted an update to their data.
        /// It may be that some updates failed - all we can say is that we tried.
        /// 
        /// Method does not throw exceptions on transient failures, merely logs and ignores them.
        /// </remarks>
        public async Task TryUpdateAsync()
        {
            using var cts = new CancellationTokenSource(Constants.MaxTotalUpdateDuration);

            // If we get this lock, we will actually perform the update.
            using var writeLock = await SemaphoreLock.TryTakeAsync(_updateLock, TimeSpan.Zero);

            if (writeLock == null)
            {
                // Otherwise, we just no-op once the one that came before has updated the data.
                await WaitForPredecessorUpdateAsync(cts.Token);
                return;
            }

            using var client = _clientConfiguration.CreateClient();

            IList<ContainerListResponse> allContainers;

            try
            {
                allContainers = await client.Containers.ListContainersAsync(new ContainersListParameters
                {
                    All = true
                }, cts.Token);
            }
            catch (Exception ex)
            {
                DockerTrackerMetrics.ListContainersErrorCount.Inc();
                _log.Error(Helpers.Debug.GetAllExceptionMessages(ex));
                _log.Debug(ex.ToString()); // Only to verbose output.

                // Errors are ignored - if we fail to get data, we just skip an update and log the failure.
                // The next update will hopefully get past the error.

                // We won't even try update the trackers if we can't even list the containers.
                // TODO: Is this wise? What if individual container data is still available?
                // Then again, if listing containers already does not work, can you expect anything to work?
                return;
            }

            DockerTrackerMetrics.ContainerCount.Set(allContainers.Count);
            SynchronizeTrackerSet(allContainers);

            // Update each tracker. We do them in parallel to minimize the total time span spent on probing.
            var updateTasks = new List<Task>();

            foreach (var tracker in _containerTrackers.Values)
                updateTasks.Add(tracker.TryUpdateAsync(client, cts.Token));

            // Only exceptions from the update calls should be terminal exceptions,
            // so it is fine not to catch anything that may be thrown here.
            await Task.WhenAll(updateTasks);
        }

        private async Task WaitForPredecessorUpdateAsync(CancellationToken cancel)
        {
            _log.Debug("Will not trigger new probe as it overlaps with existing probe.");
            using var readLock = await SemaphoreLock.TakeAsync(_updateLock, cancel);
        }

        /// <summary>
        /// Ensures that we have a tracker for every listed container
        /// and removes trackers for any containers not in the list.
        /// </summary>
        private void SynchronizeTrackerSet(IList<ContainerListResponse> allContainers)
        {
            var containerIds = allContainers.Select(c => c.ID).ToArray();
            var trackedIds = _containerTrackers.Keys.ToArray();

            // Create a tracker for any new containers.
            var newIds = containerIds.Except(trackedIds);
            foreach (var id in newIds)
            {
                _log.Debug($"Encountered container for the first time: {id}");
                _containerTrackers[id] = new ContainerTracker(id);
            }

            // Remove the trackers of any removed containers.
            var removedIds = trackedIds.Except(containerIds);
            foreach (var id in removedIds)
            {
                _log.Debug($"Tracked container no longer exists. Removing: {id}");
                var tracker = _containerTrackers[id];
                tracker.Dispose();
                _containerTrackers.Remove(id);
            }
        }

        // Synchronized - only single threaded access occurs.
        private readonly Dictionary<string, ContainerTracker> _containerTrackers = new Dictionary<string, ContainerTracker>();

        private readonly LogSource _log = Log.Default;
    }
}
