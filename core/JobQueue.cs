﻿using StackExchange.Redis;
using System.Runtime.CompilerServices;

namespace Sharpbench.Core;

public class JobQueue : IJobQueue
{
    IDatabase db;
    ISubscriber sub;
    string queueKey;
    RedisChannel jobsChannel;
    readonly AutoResetEvent jobsAvailableSignal;

    public JobQueue(IDatabase db, ISubscriber sub,string queueKey, string jobsChannel)
    {
        this.db = db;
        this.sub = sub;
        this.queueKey = queueKey;
        this.jobsChannel = new RedisChannel(jobsChannel, RedisChannel.PatternMode.Literal);
        this.jobsAvailableSignal = new AutoResetEvent(false);
    }

    public async Task SubmitJob(string jobId)
    {
        string jobKey = RedisHelpers.GetJobKey(jobId);
        await this.db.ListLeftPushAsync(this.queueKey, jobKey);
        // Publish an emppty message to signal to consumers that a new job
        // is available so that they can check the queue again
        // see: https://stackexchange.github.io/StackExchange.Redis/PipelinesMultiplexers.html#multiplexing
        await this.sub.PublishAsync(this.jobsChannel, "");
    }

    public async IAsyncEnumerable<string> ListenForJobs([EnumeratorCancellation] CancellationToken clt)
    {
        var channel = this.sub.Subscribe(this.jobsChannel);
        channel.OnMessage(_ =>
        {
            // Activate the signal when a message arrives
            // indicating that that a new job is available
            // so that the loop below can be unblocked
            // see: https://stackexchange.github.io/StackExchange.Redis/PipelinesMultiplexers.html#multiplexing
            this.jobsAvailableSignal.Set();
        });

        while (!clt.IsCancellationRequested)
        {
            string? jobId = await this.GetNextJob();
            if (jobId == null)
            {
                continue;
            }

            // hand over job to worker
            // TODO: how to control number of concurrent jobs?
            // TODO: consider using channels (see: https://www.youtube.com/watch?v=gT06qvQLtJ0)
            // or create an IAsyncEnumerable stream
            yield return jobId;
            // Instead of repeatedly trying to get the next job even when there isn't one
            // we wait for a signal that new jobs have arrived before we check again.
            // Ideally, we could have used a block pop operation to wait on the queue until a job
            // is available (see: https://redis.io/docs/data-types/lists/#blocking-operations-on-lists)
            // but the StackExchange.Redis client doesn't implement the blocking pop operations
            // because it can mess up with the underlying multiplexing/pipeline architecture
            // see: https://stackexchange.github.io/StackExchange.Redis/PipelinesMultiplexers.html#multiplexing
            // Add a timeout so that we can periodically check cancellation token for graceful shutdowns
            // or just in case a pub/sub message was not delivered (see: https://redis.io/docs/interact/pubsub/#delivery-semantics)
            this.jobsAvailableSignal.WaitOne(TimeSpan.FromSeconds(5));
        }
    }

    private async Task<string?> GetNextJob()
    {
        var result = await this.db.ListRightPopAsync(this.queueKey);
        if (result.IsNull)
        {
            return null;
        }

        return RedisHelpers.JobKeyToId(result!);
    }
}
