﻿using System.Collections.Concurrent;
using System.Net.WebSockets;
using Sharpbench.Core;

namespace SharpbenchApi;

public class RealtimeClientsNotifier
{
    ConcurrentDictionary<string, ClientEntry> realTimeClients = new();
    ILogger<RealtimeClientsNotifier> logger;

    public RealtimeClientsNotifier(ILogger<RealtimeClientsNotifier> logger)
    {
        this.logger = logger;
    }

    public Task RealTimeSyncWithClient(WebSocket client, string clientId)
    {
        logger.LogInformation($"Realtime communication established with new client {clientId}");
        var tcs = new TaskCompletionSource();
        realTimeClients.TryAdd(clientId, new ClientEntry(clientId, client, tcs));
        // the task will be complete when we detect that the connection is closed
        // or all communication has ended.
        // This prevents the caller from terminating prematurely and abruptly closing the connection
        return tcs.Task;
    }

    public async Task BroadcastMessage(JobMessage message)
    {
        // TODO: broadcast to all for simplicity, but should send messages to the right clients
        var data = new ArraySegment<byte>(message.Data, 0, message.Data.Length);
        await BroadcastRawMessage(data);
    }

    public async Task SendMessageToClient(string clientId, JobMessage message)
    {
        var data = new ArraySegment<byte>(message.Data, 0, message.Data.Length);
        await TrySendToClient(clientId, data);
    }

    public async Task CloseAllClients()
    {
        List<Task> tasks = new List<Task>();
        foreach (var entry in realTimeClients)
        {
            tasks.Add(entry.Value.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", default));
            entry.Value.TaskHandler.SetResult();
        }

        await Task.WhenAll(tasks);
        realTimeClients.Clear();
    }

    private async Task BroadcastRawMessage(ArraySegment<byte> message)
    {
        List<Task<bool>> tasks = new List<Task<bool>>();
        foreach (var kvp in realTimeClients)
        {
            var client = kvp.Value;
            tasks.Add(TrySendToClient(kvp.Key, message));
        }

        await Task.WhenAll(tasks);
    }

    async Task<bool> TrySendToClient(string clientId, ArraySegment<byte> message)
    {
        if (!realTimeClients.TryGetValue(clientId, out var clientEntry))
        {
            logger.LogWarning($"Attempted to send message to client {clientId}, but it was not found.");
            return false;
        }

        if (clientEntry.Socket.State == WebSocketState.Closed)
        {
            logger.LogInformation($"Attempted to send message to client {clientId}, but client socket was closed.");
            realTimeClients.Remove(clientId, out _);
            // complete the task being awaited
            clientEntry.TaskHandler.SetResult();
            return false;
        }

        logger.LogInformation($"Attempt to send message to client {clientId}");
        await clientEntry.Socket.SendAsync(message, WebSocketMessageType.Text, true, CancellationToken.None);
        logger.LogInformation($"Sent message to client {clientId}");
        return true;
    }
}

record ClientEntry(string ClientId, WebSocket Socket, TaskCompletionSource TaskHandler);