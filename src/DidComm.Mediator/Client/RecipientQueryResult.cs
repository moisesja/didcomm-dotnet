using DidComm.Mediator.Coordination;

namespace DidComm.Mediator.Client;

/// <summary>
/// One page of the DIDs registered with a mediator under your grant, as
/// <see cref="MediationClient.QueryRecipientsAsync"/> returns it (<c>recipient</c>; <c>keylist</c> in
/// Coordinate Mediation 2.0).
/// </summary>
public sealed class RecipientQueryResult
{
    /// <summary>Initialize a page.</summary>
    /// <param name="dids">The DIDs of the page, as the mediator listed them.</param>
    /// <param name="pagination">The page position the mediator reported, or <c>null</c>.</param>
    internal RecipientQueryResult(IReadOnlyList<string> dids, RecipientPagination? pagination)
    {
        Dids = dids;
        Pagination = pagination;
    }

    /// <summary>The DIDs of this page, in the order the mediator listed them.</summary>
    public IReadOnlyList<string> Dids { get; }

    /// <summary>
    /// Where the page sits in the whole list, when the mediator said: how many DIDs it holds, its
    /// offset, and how many remain after it. <c>null</c> when the answer carried no pagination; the
    /// client has checked that a reported position agrees with the page and the request.
    /// </summary>
    public RecipientPagination? Pagination { get; }
}
