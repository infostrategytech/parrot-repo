using Parrot.Application.Tasks;
using Parrot.Domain.Entities;

namespace Parrot.Application.Payments;

/// <summary>
/// Tells the business about a payment the way it hears about every order from a chat: an
/// Order task in Tasks, raised by the agent that made the link, on the conversation it was
/// made in. One that needs a look (a late payment, sold-out items, the wrong amount) is urgent.
/// </summary>
public sealed class TaskPaymentNotifier(TaskService tasks) : IPaymentNotifier
{
    public async Task PaidAsync(PaymentOrder order, CancellationToken ct)
    {
        bool needsLook = order.Status != PaymentOrderStatus.Paid || order.AttentionReason is not null;
        string items = string.Join(", ", order.Lines.Select(l => $"{l.Quantity} x {Label(l)}"));
        string title = (needsLook ? "Payment to check: " : "Paid: ") + items;
        if (title.Length > 120)
        {
            title = title[..117] + "...";
        }

        List<string> details = [];
        if (order.AttentionReason is not null)
        {
            details.Add(order.AttentionReason);
        }

        details.Add($"Paid NGN {CheckoutService.FromMinor(order.PaidMinor ?? 0):N2} through Accelerate (reference {order.Reference}, transaction {order.ProviderTransactionId ?? "-"}).");
        if (order.Note is not null)
        {
            details.Add($"Note from the chat: {order.Note}");
        }

        if (order.CustomerEmail is not null)
        {
            details.Add($"Email: {order.CustomerEmail}");
        }

        await tasks.RaiseAsync(order.WorkspaceId, new RaiseTaskRequest(
            order.AgentId,
            nameof(WorkTaskKind.Order),
            title,
            order.ConnectionId,
            order.CustomerSender,
            CustomerName: order.CustomerName,
            CustomerContact: order.CustomerPhone,
            Summary: string.Join("\n", details),
            Lines: order.Lines.Select(l => new WorkTaskLine(Label(l), l.Quantity, CheckoutService.FromMinor(l.UnitPriceMinor))).ToList(),
            Total: CheckoutService.FromMinor(order.TotalMinor),
            Urgent: needsLook), ct);
    }

    private static string Label(PaymentOrderLine line) => line.VariantName is null ? line.Name : $"{line.Name} ({line.VariantName})";
}
