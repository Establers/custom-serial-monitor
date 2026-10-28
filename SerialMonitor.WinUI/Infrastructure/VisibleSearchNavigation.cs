namespace SerialMonitor.WinUI.Infrastructure;

internal static class VisibleSearchNavigation
{
    public static VisibleLogSearchPosition? ResolveNavigationPosition(
        VisibleLogSearchSnapshot snapshot,
        VisibleLogSearchPosition? currentPosition,
        int movement,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.TotalMatchCount == 0)
        {
            return null;
        }

        if (movement == 0)
        {
            return currentPosition;
        }

        var movePrevious = movement < 0;
        var remaining = Math.Abs((long)movement);
        VisibleLogSearchPosition position;
        if (currentPosition.HasValue)
        {
            position = currentPosition.Value;
        }
        else
        {
            var found = movePrevious
                ? snapshot.TryGetLast(out position)
                : snapshot.TryGetFirst(out position);
            if (!found)
            {
                return null;
            }

            remaining--;
        }

        remaining %= snapshot.TotalMatchCount;
        while (remaining-- > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = movePrevious
                ? snapshot.TryGetPrevious(position, out position)
                : snapshot.TryGetNext(position, out position);
            if (!found)
            {
                return null;
            }
        }

        return position;
    }

    public static VisibleLogSearchPosition? ResolveSearchPosition(
        VisibleLogSearchSnapshot snapshot,
        int movement,
        long? previousLineId,
        int? previousPayloadOffset)
    {
        var restoredPosition = default(VisibleLogSearchPosition);
        var restored = previousLineId.HasValue && previousPayloadOffset.HasValue &&
            snapshot.TryFind(previousLineId.Value, previousPayloadOffset.Value, out restoredPosition);
        VisibleLogSearchPosition selectedPosition;
        bool selected;
        if (restored)
        {
            if (movement > 0)
            {
                selected = snapshot.TryGetNext(restoredPosition, out selectedPosition);
            }
            else if (movement < 0)
            {
                selected = snapshot.TryGetPrevious(restoredPosition, out selectedPosition);
            }
            else
            {
                return restoredPosition;
            }
        }
        else
        {
            selected = movement < 0
                ? snapshot.TryGetLast(out selectedPosition)
                : snapshot.TryGetFirst(out selectedPosition);
        }

        return selected ? selectedPosition : null;
    }

    public static VisibleLogSearchPage GetPageForPosition(
        VisibleLogSearchSnapshot snapshot,
        VisibleLogSearchPosition position,
        int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        if (position.MatchedLineIndex < 0 || position.MatchedLineIndex >= snapshot.MatchedLines.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        return snapshot.GetMatchedLinePage(position.MatchedLineIndex / pageSize, pageSize);
    }
}
