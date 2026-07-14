// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using SalesforceCopilotConnector.Salesforce;

namespace SalesforceCopilotConnector.Tests.TestSalesforce;

/// <summary>
/// Tests for <see cref="ShardHash"/> — the deterministic record-id → bucket assignment
/// behind intra-object hash sharding.
///
/// The PINNED literal expectations are the point: bucket assignment permanently decides
/// which Graph connection owns a record (items, inventory rows, reconcile scope), so the
/// function is a compatibility contract. If one of these pins ever fails, the hash
/// changed — which silently strands/duplicates every already-ingested record of every
/// hash-sharded deployment. Do NOT update the pins to make the suite pass; revert the
/// hash change (or design an explicit re-shard migration).
/// </summary>
public class ShardHashTests
{
    // ── pinned assignments (FNV-1a 64 over the first 15 UTF-16 code units) ─────

    [Theory]
    [InlineData("001A0000003DHP0", 2, 0)]
    [InlineData("001A0000003DHP0", 3, 2)]
    [InlineData("001A0000003DHP0", 5, 0)]
    [InlineData("001A0000003DHP0", 16, 0)]
    [InlineData("500B0000001xyzA", 2, 1)]
    [InlineData("500B0000001xyzA", 3, 1)]
    [InlineData("500B0000001xyzA", 5, 3)]
    [InlineData("500B0000001xyzA", 16, 3)]
    [InlineData("00QC000000AbCdE", 5, 0)]
    [InlineData("006D000000Fghij", 3, 0)]
    [InlineData("006D000000Fghij", 16, 15)]
    public void BucketAssignmentsArePinned(string recordId, int bucketCount, int expected)
    {
        Assert.Equal(expected, ShardHash.Bucket(recordId, bucketCount));
    }

    // ── 15-char and 18-char forms of the SAME record co-hash ───────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(16)]
    public void FifteenAndEighteenCharFormsLandInTheSameBucket(int bucketCount)
    {
        // 18-char = 15-char + 3-char casing checksum; both refer to one record and MUST
        // route to the same connection whichever form an API happens to hand us.
        Assert.Equal(
            ShardHash.Bucket("001A0000003DHP0", bucketCount),
            ShardHash.Bucket("001A0000003DHP0IAO", bucketCount));
    }

    [Fact]
    public void CaseSensitivePrefixesAreDistinctRecords()
    {
        // 15-char ids differing only in case are DIFFERENT records; the hash treats them
        // independently (no case folding). Collisions at some bucket counts are legal for
        // any hash (this pair collides at /16), so pin a modulus where they provably
        // differ: at /5 the lower-case form lands in 2, the upper-case in 0.
        Assert.Equal(2, ShardHash.Bucket("001a0000003dhp0", 5));
        Assert.Equal(0, ShardHash.Bucket("001A0000003DHP0", 5));
    }

    // ── range, determinism, distribution ───────────────────────────────────────

    [Fact]
    public void BucketIsAlwaysInRange()
    {
        for (var n = 1; n <= 16; n++)
            for (var i = 0; i < 200; i++)
            {
                var bucket = ShardHash.Bucket($"001A00{i:D9}", n);
                Assert.InRange(bucket, 0, n - 1);
            }
    }

    [Fact]
    public void SingleBucketAlwaysZeroAndInvalidCountThrows()
    {
        Assert.Equal(0, ShardHash.Bucket("001A0000003DHP0", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShardHash.Bucket("001A0000003DHP0", 0));
    }

    [Fact]
    public void SequentialIdsSpreadEvenlyAcrossBuckets()
    {
        // Salesforce ids are near-sequential by creation time — the worst case for naive
        // range sharding and exactly what the hash must spread. 5 000 sequential ids over
        // 5 buckets: each bucket within ±30% of the 1 000 expected.
        var counts = new int[5];
        for (var i = 0; i < 5000; i++)
            counts[ShardHash.Bucket($"001A00{i:D9}", 5)]++;
        Assert.All(counts, c => Assert.InRange(c, 700, 1300));
    }

    // ── ShardBucketSpec.Owns composes buckets + hash ────────────────────────────

    [Fact]
    public void SpecOwnsExactlyItsBuckets()
    {
        // Pinned above: /5 puts 001A0000003DHP0 in bucket 0 and 500B0000001xyzA in bucket 3.
        var spec = new ShardBucketSpec(5, new HashSet<int> { 0, 1 });
        Assert.True(spec.Owns("001A0000003DHP0"));
        Assert.False(spec.Owns("500B0000001xyzA"));
    }
}
