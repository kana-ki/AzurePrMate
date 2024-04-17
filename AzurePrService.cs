using Microsoft.Extensions.Configuration;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;

namespace AzurePRMate;

class AzurePrService(IConfiguration config): IDisposable {
        
    private const string URI = "https://dev.azure.com/PebblePad";
        
    private readonly bool _includeWaitingForAuthor = config.GetValue("IncludeWaitingForAuthor", true);
    private readonly bool _includeRejected = config.GetValue("IncludeRejected", false);
    private readonly bool _includeDraft = config.GetValue("IncludeDraft", false);
    private readonly WaitingForAuthorClearConfiguration _clearWaitingForAuthor =
        config.GetValue("ClearWaitingForAuthorVotes", WaitingForAuthorClearConfiguration.Never);
    private readonly VssConnection _connection = 
        new (new Uri(URI), new VssBasicCredential("", config.GetValue<string>("Azure:PersonalAccessToken")));

    public async Task<int> GetPullRequestCountAsync() {
        var pullRequests = await this.GetUsersPullRequestsAsync();

        return pullRequests.Count(pr => {
            if (!this._includeDraft && pr.IsDraft == true) {
                return false;
            }
            var review = pr.Reviewers.First(r => r.Id == this._connection.AuthorizedIdentity.Id.ToString());
            return !review.HasDeclined.GetValueOrDefault(false)
                   && (review.Vote == 0
                       || (this._includeWaitingForAuthor && review.Vote == -5)
                       || (this._includeRejected && review.Vote == -10));
        });
    }

    public async Task RunAutomationsAsync() {
        var gitClient = this.GetGitClient();
        await ClearWaitingForAuthorVotes(gitClient);
    }

    private async Task ClearWaitingForAuthorVotes(GitHttpClient gitClient) {
        if (this._clearWaitingForAuthor == WaitingForAuthorClearConfiguration.Never) {
            return;
        }
        var pullRequests = await this.GetUsersPullRequestsAsync();
        foreach (var pr in pullRequests) {
            IdentityRefWithVote review;
            try
            {
                review = pr.Reviewers.First(r => r.Id == this._connection.AuthorizedIdentity.Id.ToString());
            }
            catch (Exception e)
            {
                Log.Warning(e, "Could not get threads for Pull Request \"{Pull Request Title}\".", pr.Title);
                continue;
            }

            if (review.Vote == -5 /* Waiting for Author */) {
                var clear = false;
                var threads = await gitClient.GetThreadsAsync(pr.Repository.Id, pr.PullRequestId);
                if (this._clearWaitingForAuthor == WaitingForAuthorClearConfiguration.AllCommentsResolved) {
                    var allThreadsResolved = threads.All(thread => thread.IsDeleted
                                                                   || !(thread.Status == CommentThreadStatus.Active || thread.Status == CommentThreadStatus.Pending));
                    clear = allThreadsResolved;
                } else if (this._clearWaitingForAuthor == WaitingForAuthorClearConfiguration.PullRequestUpdated) {
                    var iterations = await gitClient.GetPullRequestIterationsAsync(pr.Repository.Id, pr.PullRequestId);
                    var prLastUpdated = iterations.OrderByDescending(iteration => iteration.UpdatedDate).FirstOrDefault();
                    var lastWaitingForAuthorThread = threads.OrderByDescending(thread => thread.PublishedDate).FirstOrDefault(thread => {
                        var comment = thread.Comments.First();
                        return comment.CommentType == CommentType.System
                               && comment.Content == $"{review.DisplayName} voted -5";
                    });
                    clear = prLastUpdated.UpdatedDate > lastWaitingForAuthorThread.PublishedDate;
                }
                if (clear) {
                    var newReview = new IdentityRefWithVote() { Id = review.Id, Vote = 0 };
                    try
                    {
                        await gitClient.UpdatePullRequestReviewersAsync(new[] { newReview }, pr.Repository.Id, pr.PullRequestId);
                    }
                    catch (Exception e)
                    {
                        Log.Warning(e, "Could not update review on Pull Request \"{Pull Request Title}\".", pr.Title);
                        continue;
                    }
                }
            }
        }
    }

    public async Task<List<GitPullRequest>> GetUsersPullRequestsAsync() {
        var searchCriteria = new GitPullRequestSearchCriteria() {
            Status = PullRequestStatus.Active,
            ReviewerId = this._connection.AuthorizedIdentity.Id,
        };

        return await GetPullRequests(searchCriteria);
    }

    public async Task<List<GitPullRequest>> GetPullRequests(GitPullRequestSearchCriteria searchCriteria) {
        var pullRequests = new List<GitPullRequest>();

        var client = this.GetGitClient();

        var repositories = await client.GetRepositoriesAsync();
        foreach (var repository in repositories) {
            var pullRequestsForRepo = new List<GitPullRequest>();
            try
            {
                pullRequestsForRepo = await client.GetPullRequestsAsync(repository.Id.ToString(), searchCriteria);
            } catch (VssServiceException e) {
                Log.Warning(e, "Could not get pull requests from repository \"{Repository Name}\".", repository.Name);
            }
            pullRequests.AddRange(pullRequestsForRepo);
        }

        return pullRequests;
    }
        
    private GitHttpClient GetGitClient() {
        return this._connection.GetClient<GitHttpClient>();
    }

    public void Dispose() {
        this._connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
    
enum WaitingForAuthorClearConfiguration {
    Never,
    PullRequestUpdated,
    AllCommentsResolved
}