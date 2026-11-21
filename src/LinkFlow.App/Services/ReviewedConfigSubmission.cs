using LinkFlow.Core;
using LinkFlow.Core.Ipc;

namespace LinkFlow.App.Services;

/// <summary>Reviewed imports use an explicit expected revision, never the client's automatic mutation/rebase queue.</summary>
internal static class ReviewedConfigSubmission
{
    internal static async Task<CommandReceipt> SubmitAsync(IEngineClient client, ControlConfig prepared,
        long reviewedRevision, CancellationToken cancellationToken, TimeSpan? acknowledgementTimeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var request = new EngineCommandRequest
        {
            Kind = PipeProtocol.SetConfig, ExpectedRevision = reviewedRevision, Config = prepared,
            RequestedAtUtc = now, ExpiresAtUtc = now.AddSeconds(30)
        };
        var accepted = new TaskCompletionSource<CommandReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(CommandReceipt receipt)
        {
            if (receipt.Id == request.Id && receipt.State != CommandState.Pending) accepted.TrySetResult(receipt);
        }
        client.CommandUpdated += Observe;
        try
        {
            // SubmitCommand deliberately has no rebase baseline: the service must reject a different revision.
            string id = client.SubmitCommand(request);
            if (id != request.Id) throw new InvalidOperationException("Unexpected import command identity.");
            foreach (var receipt in client.Operations) Observe(receipt);
            CommandReceipt result;
            try { result = await accepted.Task.WaitAsync(acknowledgementTimeout ?? TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException ex)
            {
                throw new InvalidOperationException(OperationalText.Pick(
                    "Não foi possível confirmar o aceite da importação. Verifique o comando em Operações antes de repetir.",
                    "Import acceptance could not be confirmed. Check the command in Operations before retrying."), ex);
            }
            if (result.State is not (CommandState.Accepted or CommandState.Applied) || result.Revision != reviewedRevision + 1)
                throw new InvalidOperationException(OperationalText.Pick("Importação não confirmada: ", "Import not confirmed: ") +
                    OperationalText.Command(result.State) + " · " + result.Message);
            return result;
        }
        finally { client.CommandUpdated -= Observe; }
    }
}
