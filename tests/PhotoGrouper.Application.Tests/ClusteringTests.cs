using FluentAssertions;
using PhotoGrouper.Application.Clustering;
using PhotoGrouper.Application.Ports;

namespace PhotoGrouper.Application.Tests;

/// <summary>
/// Covers the grouping algorithm.
/// </summary>
/// <remarks>
/// Tested against neighbour lists written out by hand rather than against real embeddings. The
/// algorithm's job is to turn "these faces resemble each other" into "these faces are one person",
/// and that logic is independent of where the resemblance scores came from. Keeping the test at
/// that level means it runs in milliseconds, needs no model, and fails for exactly one reason.
///
/// The constraint cases matter most. They are what stops a re-run from discarding the corrections
/// a user made by hand, and a regression there would not be visible until somebody noticed the
/// app had forgotten what they told it.
/// </remarks>
public sealed class ChineseWhispersTests
{
    /// <summary>Builds neighbour lists from an adjacency description.</summary>
    private static Neighbour[][] Graph(int count, params (int A, int B, float Similarity)[] edges)
    {
        var lists = new List<Neighbour>[count];
        for (var i = 0; i < count; i++)
        {
            lists[i] = [];
        }

        foreach (var (a, b, similarity) in edges)
        {
            lists[a].Add(new Neighbour(b, similarity));
        }

        return [.. lists.Select(l => l.ToArray())];
    }

    private static int GroupCount(int[] labels) => labels.Distinct().Count();

    [Fact]
    public void An_empty_graph_produces_no_labels() =>
        ChineseWhispers.Cluster([]).Should().BeEmpty();

    [Fact]
    public void Faces_that_match_nothing_are_kept_rather_than_discarded()
    {
        // Regression. One-off faces used to be dropped on the reasoning that a face matching
        // nothing is usually a stranger. On a small library that silently hid a third of the
        // faces, including large, confident detections of people who simply appear once, with
        // nowhere in the application to see them. They are grouped now and shown separately.
        var labels = ChineseWhispers.Cluster(Graph(5, (0, 1, 0.9f)));

        labels.Should().HaveCount(5, "every face gets a label, including the ones matching nothing");
        GroupCount(labels).Should().Be(4, "two faces join up; the other three stand alone");
    }

    [Fact]
    public void A_face_with_no_neighbours_keeps_its_own_group()
    {
        // The right answer for somebody photographed once. Forcing them into a group with the
        // nearest stranger would be worse than leaving them alone.
        var labels = ChineseWhispers.Cluster(Graph(3));

        GroupCount(labels).Should().Be(3);
    }

    [Fact]
    public void Strongly_connected_faces_become_one_group()
    {
        var labels = ChineseWhispers.Cluster(Graph(4,
            (0, 1, 0.9f), (1, 2, 0.9f), (2, 3, 0.9f), (3, 0, 0.9f)));

        GroupCount(labels).Should().Be(1);
        labels.Distinct().Should().ContainSingle();
    }

    [Fact]
    public void Two_separate_groups_stay_separate()
    {
        var labels = ChineseWhispers.Cluster(Graph(6,
            (0, 1, 0.9f), (1, 2, 0.9f), (2, 0, 0.9f),
            (3, 4, 0.9f), (4, 5, 0.9f), (5, 3, 0.9f)));

        GroupCount(labels).Should().Be(2);
        labels[0].Should().Be(labels[1]).And.Be(labels[2]);
        labels[3].Should().Be(labels[4]).And.Be(labels[5]);
        labels[0].Should().NotBe(labels[3]);
    }

    [Fact]
    public void A_single_weak_link_does_not_merge_two_dense_groups()
    {
        // The everyday case of two people photographed together: one shot links them, but the
        // weight of each person's own photographs should outvote it.
        var labels = ChineseWhispers.Cluster(Graph(8,
            (0, 1, 0.9f), (1, 2, 0.9f), (2, 3, 0.9f), (3, 0, 0.9f), (0, 2, 0.9f), (1, 3, 0.9f),
            (4, 5, 0.9f), (5, 6, 0.9f), (6, 7, 0.9f), (7, 4, 0.9f), (4, 6, 0.9f), (5, 7, 0.9f),
            (3, 4, 0.36f)));

        GroupCount(labels).Should().Be(2);
    }

    [Fact]
    public void Edges_are_treated_as_going_both_ways()
    {
        // The index reports each face's strongest matches, which is not a symmetric relation: a
        // face in a crowd may rate a portrait among its best matches without the reverse holding.
        // Left directed, the result would depend on which face happened to be visited first.
        var oneWay = ChineseWhispers.Cluster(Graph(2, (0, 1, 0.9f)));

        GroupCount(oneWay).Should().Be(1);
    }

    [Fact]
    public void The_same_input_always_produces_the_same_grouping()
    {
        // Visiting order genuinely changes the outcome, so it is seeded. A user re-running the
        // grouping and getting different people would have no way to tell that from a defect.
        var graph = Graph(10,
            (0, 1, 0.8f), (1, 2, 0.7f), (2, 3, 0.9f), (4, 5, 0.85f),
            (5, 6, 0.75f), (7, 8, 0.95f), (8, 9, 0.6f), (3, 4, 0.4f));

        var first = ChineseWhispers.Cluster(graph);
        var second = ChineseWhispers.Cluster(graph);

        second.Should().Equal(first);
    }

    [Fact]
    public void A_must_link_joins_faces_the_scores_would_have_separated()
    {
        // The same person in two photographs that do not resemble each other: different lighting,
        // years apart, a beard. The user said they are the same, and that must win.
        var graph = Graph(4, (0, 1, 0.9f), (2, 3, 0.9f));
        var constraints = new ClusterConstraints([(0, 2)], new HashSet<(int, int)>());

        var labels = ChineseWhispers.Cluster(graph, constraints);

        labels[0].Should().Be(labels[2]);
    }

    [Fact]
    public void A_cannot_link_separates_faces_however_similar_they_are()
    {
        // Siblings, or a parent and child. No similarity score should overrule a user who has
        // looked at both photographs and said they are different people.
        var graph = Graph(2, (0, 1, 0.99f));
        var constraints = new ClusterConstraints([], new HashSet<(int, int)> { (0, 1) });

        var labels = ChineseWhispers.Cluster(graph, constraints);

        labels[0].Should().NotBe(labels[1]);
    }

    [Fact]
    public void A_cannot_link_applies_whichever_order_the_pair_is_given_in()
    {
        var graph = Graph(2, (1, 0, 0.99f));
        var constraints = new ClusterConstraints([], new HashSet<(int, int)> { (0, 1) });

        ChineseWhispers.Cluster(graph, constraints)[0]
            .Should().NotBe(ChineseWhispers.Cluster(graph, constraints)[1]);
    }

    [Fact]
    public void Labels_are_numbered_densely_from_zero()
    {
        var labels = ChineseWhispers.Cluster(Graph(6,
            (0, 1, 0.9f), (2, 3, 0.9f), (4, 5, 0.9f)));

        labels.Distinct().OrderBy(x => x).Should().Equal(0, 1, 2);
    }

    [Fact]
    public void A_long_chain_of_similar_faces_becomes_one_person()
    {
        // Someone photographed over years, where consecutive photographs resemble each other but
        // the first and last do not. Transitivity through the chain is what recovers them.
        var edges = Enumerable.Range(0, 19).Select(i => (i, i + 1, 0.7f)).ToArray();

        GroupCount(ChineseWhispers.Cluster(Graph(20, edges))).Should().Be(1);
    }

    [Fact]
    public void A_large_library_groups_without_mixing_people_together()
    {
        // Fifty people of forty faces each, connected at random within each person. The property
        // that matters is purity, not the exact number of groups: splitting one person into two
        // costs the user a merge, whereas merging two people costs them a mistake they may never
        // notice. The algorithm is randomised, so pinning the count exactly would be brittle and
        // would say nothing about which kind of error occurred.
        const int people = 50;
        const int facesEach = 40;

        var random = new Random(7);
        var edges = new List<(int, int, float)>();

        for (var person = 0; person < people; person++)
        {
            for (var i = 0; i < facesEach; i++)
            {
                for (var j = 0; j < 5; j++)
                {
                    var a = (person * facesEach) + i;
                    var b = (person * facesEach) + random.Next(facesEach);
                    if (a != b)
                    {
                        edges.Add((a, b, 0.8f));
                    }
                }
            }
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var labels = ChineseWhispers.Cluster(Graph(people * facesEach, [.. edges]));
        watch.Stop();

        var truePerson = (int face) => face / facesEach;
        var mixed = labels
            .Select((label, face) => (label, person: truePerson(face)))
            .GroupBy(x => x.label)
            .Where(g => g.Select(x => x.person).Distinct().Count() > 1)
            .ToList();

        mixed.Should().BeEmpty("merging two people is the error a user is least likely to catch");
        GroupCount(labels).Should().BeInRange(people, people + 5,
            "some splitting is tolerable; wholesale fragmentation is not");
        watch.ElapsedMilliseconds.Should().BeLessThan(2000);
    }
}
