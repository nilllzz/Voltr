namespace NetVoltr
{
    public enum ChannelState
    {
        Initial, // for channels with no initial name, not yet subscribed
        Holding, // channel waiting to get its name assigned after subscribing
        Subscribed,
        Unsubscribed,
        Errored // for channels with not initial name, failed to create
    }
}
