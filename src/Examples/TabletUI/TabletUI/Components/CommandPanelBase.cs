using Microsoft.AspNetCore.Components;
using TabletUI.Services;

namespace TabletUI.Components;

public abstract class CommandPanelBase : ComponentBase
{
    [Parameter, EditorRequired]
    public FeedConnection Connection { get; set; } = default!;

    protected bool Sending { get; private set; }
    protected string? SendFeedback { get; private set; }
    protected bool SendFailed { get; private set; }
    protected bool SendDisabled => !Connection.CanSend || Sending;

    protected async Task SendAsync(string body, string contentType)
    {
        if (SendDisabled)
        {
            return;
        }

        Sending = true;
        SendFeedback = null;
        SendFailed = false;
        try
        {
            var count = await Connection.PublishAsync(body, contentType);
            SendFailed = count is null;
            SendFeedback = count switch
            {
                null => "Send could not be confirmed. See Debug before trying again.",
                0 => "POST accepted by 0 reader queues. No readers were waiting for messages.",
                1 => "POST accepted by 1 reader queue.",
                _ => $"POST accepted by {count} reader queues."
            };
        }
        catch (Exception)
        {
            // Exception messages can contain request data; never render them.
            SendFailed = true;
            SendFeedback = "Send could not be confirmed. See Debug before trying again.";
        }
        finally
        {
            Sending = false;
        }
    }
}
