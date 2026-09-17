using Grpc.Core;
using Parrot.Grpc.Contracts.Messaging.V1;
using Parrot.Web.Features.Dashboard.Channels.Models;
using Parrot.Web.Features.Auth.State;

namespace Parrot.Web.Features.Dashboard.Channels.Services;

public class MessageVerificationGrpcClient : IMessageVerificationGrpcClient
{
    private readonly MessageVerificationService.MessageVerificationServiceClient _client;
    private readonly AuthState _authState;

    public MessageVerificationGrpcClient(MessageVerificationService.MessageVerificationServiceClient client, AuthState authState)
    {
        _client = client;
        _authState = authState;
    }

    public async Task<MessageCheckResult> CheckForMessageAsync(
        string channelId,
        long sinceUnixSeconds,
        string platform,
        string bearerToken,
        CancellationToken cancellationToken = default)
    {
        var headers = new Metadata { { "Authorization", $"Bearer {bearerToken}" } };
        CheckForMessageResponse response;
        try
        {
            response = await _client.CheckForMessageAsync(
            new CheckForMessageRequest
            {
                ChannelId = channelId,
                SinceUnixSeconds = sinceUnixSeconds,
                Platform = platform,
            },
            new CallOptions(headers: headers, cancellationToken: cancellationToken));
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
        {
            _authState.ReportUnauthorized(bearerToken);
            throw;
        }

        if (!response.MessageReceived)
            return new MessageCheckResult(false, null, null);

        DateTime receivedAt = DateTimeOffset.FromUnixTimeSeconds(response.ReceivedAtUnixSeconds).UtcDateTime;
        return new MessageCheckResult(true, response.FromNumber, receivedAt);
    }
}
