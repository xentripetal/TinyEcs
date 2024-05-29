namespace TinyEcs;

static class Match
{
	public static int Validate(IComparer<ulong> comparer, EcsID[] ids, ReadOnlySpan<QueryTerm> terms)
	{
		if (terms.IsEmpty)
			return -1;

        foreach (var term in terms)
        {
            switch (term.Op)
            {
                case TermOp.With:
                    if (ids.All(id => ComponentComparer.CompareTerms(null, id, term.Id) != 0))
                    {
                        return 1; // Required ID not found
                    }
                    break;
                case TermOp.Without:
                    if (ids.Any(id => ComponentComparer.CompareTerms(null, id, term.Id) == 0))
                    {
                        return -1; // Forbidden ID found
                    }
                    break;
                case TermOp.Optional:
                    // Do nothing, as presence or absence is acceptable
                    break;
                // case TermOp.AtLeastOne:
                //     if (!ids.Any(id => term.IDs.Any(tid => ComponentComparer.CompareTerms(null, id, tid.ID)== 0)))
                //     {
                //         return 1; // At least one required ID not found
                //     }
                //     break;
                // case TermOp.Exactly:
                //     if (!ids.SequenceEqual(term.IDs.Select(s => s.ID)))
                //     {
                //         return 1; // Exact match required but not found
                //     }
                //     break;
                // case TermOp.None:
                //     if (ids.Any(id => term.IDs.Any(tid => ComponentComparer.CompareTerms(null, id, tid.ID) == 0)))
                //     {
                //         return -1; // None of the specified IDs should be present
                //     }
                //     break;
				case TermOp.Or:
					// Or is applied on query side
					break;
            }
        }

        return 0;
	}
}
