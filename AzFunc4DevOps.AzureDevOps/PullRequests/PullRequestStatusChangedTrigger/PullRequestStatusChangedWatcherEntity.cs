using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace AzFunc4DevOps.AzureDevOps
{
    public class PullRequestStatusChangedWatcherEntity : IGenericWatcherEntity<GenericWatcherEntityParams>
    {
        #region Entity State

        public Dictionary<int, PullRequestStatusStruct> CurrentStatuses { get; set; }

        #endregion

        public PullRequestStatusChangedWatcherEntity(ILogger log, VssConnection connection, TriggerExecutorRegistry executorRegistry)
        {
            this._connection = connection;
            this._executorRegistry = executorRegistry;
            this._log = log;
        }

        public async Task Watch(GenericWatcherEntityParams watcherParams)
        {
            var attribute = (PullRequestStatusChangedTriggerAttribute)this._executorRegistry.TryGetTriggerAttributeForEntity(Entity.Current.EntityId);
            if (attribute == null)
            {
                return;
            }

            var searchCriteria = new GitPullRequestSearchCriteria
            {
                SourceRefName = this.FixBranchName(attribute.SourceBranch),
                TargetRefName = this.FixBranchName(attribute.TargetBranch)
            };

            var shouldBeTriggeredOnlyOnce = (!string.IsNullOrWhiteSpace(attribute.FromValue)) || (!string.IsNullOrWhiteSpace(attribute.ToValue));

            var client = await this._connection.GetClientAsync<GitHttpClient>();

            // Storing here the items which function invocation failed for. So that they are only retried during next polling session.
            var failedIds = new HashSet<int>();
            while (true)
            {
                var pullRequests = (await client.GetPullRequestsByProjectAsync(attribute.ProjectName, searchCriteria))
                    .Where(pr => string.IsNullOrWhiteSpace(attribute.Repository) ? 
                        true : 
                        pr.Repository.Name == attribute.Repository)
                    .ToList();

                if (this.CurrentStatuses == null)
                {
                    // At first run just saving the current snapshot and quitting
                    this.CurrentStatuses = pullRequests.ToDictionary(
                        b => b.PullRequestId, 
                        b => new PullRequestStatusStruct{ Status = this.ConvertStatus(b) }
                    );
                    return;
                }

                var newStatuses = new Dictionary<int, PullRequestStatusStruct>();

                foreach (var pullRequest in pullRequests)
                {
                    var newStatus = this.ConvertStatus(pullRequest);

                    this.CurrentStatuses.TryGetValue(pullRequest.PullRequestId, out var curStatus);
                    // Need to reset the completion flag, if pullRequest was reverted to draft state
                    if (curStatus.AlreadyTriggered && curStatus.Status != newStatus && newStatus == PullRequestStatusEnum.Draft)
                    {
                        curStatus = new PullRequestStatusStruct();
                    }

                    if (!curStatus.AlreadyTriggered && newStatus != curStatus.Status && !failedIds.Contains(pullRequest.PullRequestId))
                    {
                        try
                        {
                            if (this.CheckIfShouldBeTriggered(attribute, pullRequest))
                            {
                                // Intentionally using await, to distribute the load against Azure DevOps
                                await this.InvokeFunction(pullRequest);

                                if (shouldBeTriggeredOnlyOnce) 
                                {
                                    // Marking that function has already been triggered for this item
                                    curStatus.AlreadyTriggered = true;
                                }
                            }

                            // Bumping up the known version
                            curStatus.Status = newStatus;
                        }
                        catch (Exception ex)
                        {                            
                            this._log.LogError(ex, $"PullRequestStatusChangedTrigger failed for pull request #{pullRequest.PullRequestId}");

                            // Memorizing this item as failed, so that it is only retried next time.
                            failedIds.Add(pullRequest.PullRequestId);
                        }
                    }

                    newStatuses[pullRequest.PullRequestId] = curStatus;
                }

                // Setting new state
                this.CurrentStatuses = newStatuses;

                // Explicitly persisting current state
                Entity.Current.SetState(this);

                if (DateTimeOffset.UtcNow > watcherParams.WhenToStop) 
                {
                    // Quitting, if it's time to stop
                    return;
                }

                // Delay until next attempt
                await Global.DelayForAboutASecond();
            }
        }

        public void Delete()
        {
            Entity.Current.DeleteState();
        }

        private readonly VssConnection _connection;
        private readonly TriggerExecutorRegistry _executorRegistry;
        private readonly ILogger _log;

        private bool CheckIfShouldBeTriggered(PullRequestStatusChangedTriggerAttribute attr, GitPullRequest pullRequest)
        {
            bool isChanged = true;

            var status = this.ConvertStatus(pullRequest);

            if (!string.IsNullOrWhiteSpace(attr.FromValue))
            {
                var fromStatus = (PullRequestStatusEnum)Enum.Parse(typeof(PullRequestStatusEnum), attr.FromValue);

                // Checking that current status is _more_ than fromStatus
                switch (fromStatus)
                {
                    case PullRequestStatusEnum.Draft:
                        isChanged = isChanged && status.In(PullRequestStatusEnum.Active, PullRequestStatusEnum.Completed, PullRequestStatusEnum.Abandoned);
                    break;
                    case PullRequestStatusEnum.Active:
                        isChanged = isChanged && status.In(PullRequestStatusEnum.Completed, PullRequestStatusEnum.Abandoned);
                    break;
                    case PullRequestStatusEnum.Completed:
                    case PullRequestStatusEnum.Abandoned:
                        isChanged = false;
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(attr.ToValue))
            {
                var toStatus = (PullRequestStatusEnum)Enum.Parse(typeof(PullRequestStatusEnum), attr.ToValue);

                // Checking that current status is _more_or_equal_ than toStatus
                switch (toStatus)
                {
                    case PullRequestStatusEnum.NotSet:
                        isChanged = false;
                    break;
                    case PullRequestStatusEnum.Draft:
                        isChanged = isChanged && status.In(PullRequestStatusEnum.Draft, PullRequestStatusEnum.Active, PullRequestStatusEnum.Completed, PullRequestStatusEnum.Abandoned);
                    break;
                    case PullRequestStatusEnum.Active:
                        isChanged = isChanged && status.In(PullRequestStatusEnum.Active, PullRequestStatusEnum.Completed, PullRequestStatusEnum.Abandoned);
                    break;
                    case PullRequestStatusEnum.Completed:
                        isChanged = isChanged && status.In(PullRequestStatusEnum.Completed);
                    break;
                    case PullRequestStatusEnum.Abandoned:
                        isChanged = isChanged && status.In(PullRequestStatusEnum.Abandoned);
                    break;
                }
            }

            return isChanged;
        }

        private async Task InvokeFunction(GitPullRequest pullRequest)
        {
            var executor = this._executorRegistry.GetExecutorForEntity(Entity.Current.EntityId);

            var data = new TriggeredFunctionData()
            {
                TriggerValue = pullRequest
            };

            var result = await executor.TryExecuteAsync(data, CancellationToken.None);

            if (!result.Succeeded && result.Exception != null)
            {
                throw result.Exception;
            }
        }

        private string FixBranchName(string branchName)
        {
            if (string.IsNullOrWhiteSpace(branchName))
            {
                return null;
            }

            if (branchName.Contains('/'))
            {
                return branchName;
            }

            return $"refs/heads/{branchName}";
        }

        private PullRequestStatusEnum ConvertStatus(GitPullRequest pullRequest)
        {
            if (pullRequest.IsDraft == true)
            {
                return PullRequestStatusEnum.Draft;
            }

            return (PullRequestStatusEnum)pullRequest.Status;
        }


        // Required boilerplate
        [FunctionName(Global.FunctionPrefix + nameof(PullRequestStatusChangedWatcherEntity))]
        public static Task Run(
            [EntityTrigger] IDurableEntityContext ctx,
            ILogger log
        ) => ctx.DispatchAsync<PullRequestStatusChangedWatcherEntity>(log);
    }
}