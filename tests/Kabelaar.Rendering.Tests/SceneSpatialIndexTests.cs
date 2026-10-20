using Kabelaar.Core;
using Xunit;

namespace Kabelaar.Rendering.Tests;

public sealed class SceneSpatialIndexTests
{
    [Fact]
    public void SuccessiveMovesAndUndoKeepEarlierChangesAndEarlierIndexesIntact()
    {
        var original = Grid(40);
        var initial = new SceneSpatialIndex(original);
        var firstItems = original.ToArray();
        firstItems[0] = Move(firstItems[0], 500, 300);
        var first = new SceneSpatialIndex(firstItems, initial);
        var secondItems = firstItems.ToArray();
        secondItems[0] = Move(secondItems[0], 75, -50);
        secondItems[1] = Move(secondItems[1], -400, 350);
        var second = new SceneSpatialIndex(secondItems, first);
        var thirdItems = secondItems.ToArray();
        thirdItems[2] = Move(thirdItems[2], 250, -300);
        var third = new SceneSpatialIndex(thirdItems, second);
        var undoItems = thirdItems.ToArray();
        undoItems[0] = original[0];
        var undo = new SceneSpatialIndex(undoItems, third);
        var movedAgainItems = undoItems.ToArray();
        movedAgainItems[0] = Move(original[0], -125, -125);
        var movedAgain = new SceneSpatialIndex(movedAgainItems, undo);

        // Probe every position from every generation, including locations now left empty.
        var probes = Probes(original.Concat(firstItems).Concat(secondItems).Concat(thirdItems)
            .Concat(undoItems).Concat(movedAgainItems)).ToArray();
        AssertMatchesBruteForce(initial, original, probes);
        AssertMatchesBruteForce(first, firstItems, probes);
        AssertMatchesBruteForce(second, secondItems, probes);
        AssertMatchesBruteForce(third, thirdItems, probes);
        AssertMatchesBruteForce(undo, undoItems, probes);
        AssertMatchesBruteForce(movedAgain, movedAgainItems, probes);
    }

    [Fact]
    public void ReorderingAndReplacementPreserveCurrentZOrderAndLockState()
    {
        var original = Grid(40);
        original[0] = Box("under", new(975, 975, 50, 50));
        original[1] = Box("over", new(975, 975, 50, 50), locked: true);
        var initial = new SceneSpatialIndex(original);
        var reordered = original.ToArray();
        (reordered[0], reordered[1]) = (reordered[1], reordered[0]);
        (reordered[2], reordered[3]) = (reordered[3], reordered[2]);
        reordered[4] = Box("replacement", original[4].Bounds);
        var updated = new SceneSpatialIndex(reordered, initial);
        var probes = Probes(original.Concat(reordered)).Append(new SceneBounds(999, 999, 2, 2)).ToArray();

        AssertMatchesBruteForce(initial, original, probes);
        AssertMatchesBruteForce(updated, reordered, probes);
        Assert.Equal(new[] { "under", "over" },
            updated.HitTest(new(1000, 1000), includeLocked: true).Select(p => p.EntityId));
        Assert.Equal(new[] { "under" }, updated.HitTest(new(1000, 1000)).Select(p => p.EntityId));
    }

    [Fact]
    public void SmallAndLargeBoundsTransitionsRemainQueryableAcrossCellBoundaries()
    {
        var original = Grid(40);
        original[0] = Box("small", new(-25, -25, 0, 0));
        original[1] = Box("large", new(-500, -500, 1000, 1000));
        var initial = new SceneSpatialIndex(original);
        var swapped = original.ToArray();
        swapped[0] = Box("small", new(-750, -750, 1500, 1500));
        swapped[1] = Box("large", new(25, 25, 0, 0));
        var updated = new SceneSpatialIndex(swapped, initial);
        var returned = new SceneSpatialIndex(original, updated);
        var probes = Probes(original.Concat(swapped)).Concat(new[]
        {
            new SceneBounds(-25, -25, 0, 0),
            new SceneBounds(25, 25, 0, 0),
            new SceneBounds(-750, -750, 0, 0),
            new SceneBounds(750, 750, 0, 0),
            new SceneBounds(-700, 600, 1, 1),
            new SceneBounds(-10000, -10000, 20000, 20000)
        }).ToArray();

        AssertMatchesBruteForce(initial, original, probes);
        AssertMatchesBruteForce(updated, swapped, probes);
        AssertMatchesBruteForce(returned, original, probes);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(40, 10)]
    [InlineData(40, 11)]
    [InlineData(1100, 256)]
    [InlineData(1100, 257)]
    public void BoundedOverlayAndRebuildMatchBruteForceWithoutChangingPreviousIndexes(int count, int changedCount)
    {
        var original = Grid(count);
        var initial = new SceneSpatialIndex(original);
        var moved = original.ToArray();
        for (var i = 0; i < changedCount; i++) moved[i] = Move(moved[i], 1000, -500);
        var updated = new SceneSpatialIndex(moved, initial);
        var nextItems = moved.ToArray();
        if (count > 0) nextItems[^1] = Move(nextItems[^1], -1000, 500);
        var next = new SceneSpatialIndex(nextItems, updated);
        var probes = Probes(original.Take(16).Concat(moved.Take(16))
            .Concat(original.Skip(Math.Max(0, changedCount - 2)).Take(3))
            .Concat(moved.Skip(Math.Max(0, changedCount - 2)).Take(3))
            .Concat(original.TakeLast(2)).Concat(nextItems.TakeLast(2))).ToArray();

        AssertMatchesBruteForce(initial, original, probes);
        AssertMatchesBruteForce(updated, moved, probes);
        AssertMatchesBruteForce(next, nextItems, probes);
    }

    [Fact]
    public void CountChangesRebuildWithoutRetainingRemovedCandidates()
    {
        var original = Grid(40);
        var initial = new SceneSpatialIndex(original);
        ScenePrimitive[] added = [.. original, Box("added", new(-500, 200, 20, 20))];
        var afterAdd = new SceneSpatialIndex(added, initial);
        var removed = added.Where((_, i) => i != 0 && i != 20).ToArray();
        var afterRemove = new SceneSpatialIndex(removed, afterAdd);
        var empty = new SceneSpatialIndex([], afterRemove);
        var restored = new SceneSpatialIndex(original, empty);
        var probes = Probes(added).ToArray();

        AssertMatchesBruteForce(initial, original, probes);
        AssertMatchesBruteForce(afterAdd, added, probes);
        AssertMatchesBruteForce(afterRemove, removed, probes);
        AssertMatchesBruteForce(empty, [], probes);
        AssertMatchesBruteForce(restored, original, probes);
    }

    [Fact]
    public void MixedGeometryHitTestsMatchFreshIndexAcrossSuccessiveMoves()
    {
        var primitives = Grid(40);
        primitives[0] = new ScenePath("path", "test", [new(-25, -25), new(25, 25)]);
        primitives[1] = new ScenePath("filled", "test",
            [new(-30, -30), new(30, -30), new(30, 30), new(-30, 30)], Fill: "#123456", Closed: true);
        primitives[2] = new SceneEllipse("ellipse", "test", new(0, 0), 20, 10);
        primitives[3] = new SceneArc("arc", "test", new(0, 0), 25, 0, 180);
        var index = new SceneSpatialIndex(primitives);
        for (var step = 1; step <= 3; step++)
        {
            var offset = step * 50;
            var moved = primitives.ToArray();
            moved[0] = new ScenePath("path", "test", [new(-25 + offset, -25), new(25 + offset, 25)]);
            moved[2] = new SceneEllipse("ellipse", "test", new(offset, 0), 20, 10);
            var updated = new SceneSpatialIndex(moved, index);
            var fresh = new SceneSpatialIndex(moved);
            for (var x = -50; x <= 200; x += 10)
                for (var y = -40; y <= 40; y += 10)
                    Assert.Equal(fresh.HitTest(new(x, y)), updated.HitTest(new(x, y)));
            primitives = moved;
            index = updated;
        }
    }

    private static ScenePrimitive[] Grid(int count) => Enumerable.Range(0, count)
        .Select(i => Box($"box-{i}", new((i % 10) * 40 - 200, (i / 10) * 40 - 80, 8, 8), i % 7 == 0)).ToArray();

    private static ScenePrimitive Box(string id, SceneBounds bounds, bool locked = false) =>
        new SceneImage(id, "test", [], bounds) { Locked = locked };

    private static ScenePrimitive Move(ScenePrimitive primitive, double dx, double dy) =>
        Box(primitive.EntityId, primitive.Bounds with { X = primitive.Bounds.X + dx, Y = primitive.Bounds.Y + dy }, primitive.Locked);

    private static IEnumerable<SceneBounds> Probes(IEnumerable<ScenePrimitive> primitives)
    {
        yield return new(-20000, -20000, 40000, 40000);
        yield return new(20000, 20000, 2, 2);
        foreach (var primitive in primitives)
        {
            var b = primitive.Bounds;
            yield return new(b.Left + b.Width / 2, b.Top + b.Height / 2, 0, 0);
            yield return new(b.Left - 1, b.Top - 1, 2, 2);
            yield return new(b.Right, b.Bottom, 0, 0);
        }
    }

    private static void AssertMatchesBruteForce(SceneSpatialIndex index, IReadOnlyList<ScenePrimitive> primitives,
        IEnumerable<SceneBounds> probes)
    {
        foreach (var bounds in probes)
        {
            Assert.Equal(primitives.Where(p => p.Bounds.Intersects(bounds)), index.Query(bounds));
            var point = new Point2(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            const double tolerance = 1.5;
            foreach (var includeLocked in new[] { false, true })
            {
                var expected = primitives.Where(p => (includeLocked || !p.Locked) && p.Bounds.Inflate(tolerance).Contains(point))
                    .Reverse();
                Assert.Equal(expected, index.HitTest(point, tolerance, includeLocked));
            }
        }
    }
}
