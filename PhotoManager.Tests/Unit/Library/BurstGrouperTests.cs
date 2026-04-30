using PhotoManager.Core.Library;
using PhotoManager.Core.Metadata;

namespace PhotoManager.Tests.Unit.Library;

[TestFixture]
public class BurstGrouperTests {
  private DirectoryInfo _root = null!;

  [SetUp]
  public void Setup() {
    this._root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-burst-" + Guid.NewGuid().ToString("N")));
    this._root.Create();
  }

  [TearDown]
  public void Teardown() {
    if (this._root.Exists)
      this._root.Delete(recursive: true);
  }

  private (FileInfo File, FullMetadata Metadata) Entry(string name, DateTime captured) {
    var path = Path.Combine(this._root.FullName, name);
    File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
    var fi = new FileInfo(path);
    var md = new FullMetadata { DateCreated = captured };
    return (fi, md);
  }

  [Test]
  public void EmptyInput_ReturnsEmpty() {
    var result = BurstGrouper.GroupBursts(Array.Empty<(FileInfo, FullMetadata)>());
    Assert.That(result, Is.Empty);
  }

  [Test]
  public void FivePhotosOneSecondGaps_FormSingleBurst() {
    var t0 = new DateTime(2024, 6, 1, 10, 0, 0);
    var entries = new[] {
      this.Entry("IMG_1001.jpg", t0),
      this.Entry("IMG_1002.jpg", t0.AddSeconds(1)),
      this.Entry("IMG_1003.jpg", t0.AddSeconds(2)),
      this.Entry("IMG_1004.jpg", t0.AddSeconds(3)),
      this.Entry("IMG_1005.jpg", t0.AddSeconds(4)),
    };

    var result = BurstGrouper.GroupBursts(entries);

    Assert.Multiple(() => {
      Assert.That(result, Has.Count.EqualTo(1));
      Assert.That(result[0].Members, Has.Count.EqualTo(5));
      Assert.That(result[0].From, Is.EqualTo(t0));
      Assert.That(result[0].To, Is.EqualTo(t0.AddSeconds(4)));
      Assert.That(result[0].SuggestedName, Does.Contain("IMG_100"));
    });
  }

  [Test]
  public void FivePhotosTenSecondGaps_AreFiveSingletons() {
    var t0 = new DateTime(2024, 6, 1, 10, 0, 0);
    var entries = new[] {
      this.Entry("IMG_1001.jpg", t0),
      this.Entry("IMG_1002.jpg", t0.AddSeconds(10)),
      this.Entry("IMG_1003.jpg", t0.AddSeconds(20)),
      this.Entry("IMG_1004.jpg", t0.AddSeconds(30)),
      this.Entry("IMG_1005.jpg", t0.AddSeconds(40)),
    };

    var result = BurstGrouper.GroupBursts(entries);

    Assert.Multiple(() => {
      Assert.That(result, Has.Count.EqualTo(5));
      Assert.That(result.All(g => g.Members.Count == 1), Is.True);
    });
  }

  [Test]
  public void MixedThreeCloseThenGapThenFourClose_ProducesTwoBursts() {
    var t0 = new DateTime(2024, 6, 1, 10, 0, 0);
    var entries = new[] {
      this.Entry("IMG_2001.jpg", t0),
      this.Entry("IMG_2002.jpg", t0.AddSeconds(1)),
      this.Entry("IMG_2003.jpg", t0.AddSeconds(2)),
      this.Entry("IMG_2010.jpg", t0.AddMinutes(5)),
      this.Entry("IMG_2011.jpg", t0.AddMinutes(5).AddSeconds(1)),
      this.Entry("IMG_2012.jpg", t0.AddMinutes(5).AddSeconds(2)),
      this.Entry("IMG_2013.jpg", t0.AddMinutes(5).AddSeconds(3)),
    };

    var result = BurstGrouper.GroupBursts(entries);

    Assert.Multiple(() => {
      Assert.That(result, Has.Count.EqualTo(2));
      Assert.That(result[0].Members, Has.Count.EqualTo(3));
      Assert.That(result[1].Members, Has.Count.EqualTo(4));
    });
  }

  [Test]
  public void SameCaptureTimeButUnrelatedNames_AreSeparateBursts() {
    var t = new DateTime(2024, 6, 1, 10, 0, 0);
    var entries = new[] {
      this.Entry("IMG_1234.jpg", t),
      this.Entry("DSC09876.jpg", t.AddSeconds(1)),
    };

    var result = BurstGrouper.GroupBursts(entries, filenameSimilarityThreshold: 3);

    Assert.That(result, Has.Count.EqualTo(2));
  }

  [Test]
  public void SuggestedName_ForRangeFromConsecutiveFrames_IncludesEllipsisRange() {
    var t0 = new DateTime(2024, 6, 1, 10, 0, 0);
    var entries = new[] {
      this.Entry("IMG_1234.jpg", t0),
      this.Entry("IMG_1235.jpg", t0.AddSeconds(1)),
      this.Entry("IMG_1236.jpg", t0.AddSeconds(2)),
    };

    var result = BurstGrouper.GroupBursts(entries);

    Assert.Multiple(() => {
      Assert.That(result, Has.Count.EqualTo(1));
      Assert.That(result[0].SuggestedName, Is.EqualTo("IMG_1234..1236"));
    });
  }

  [Test]
  public void SinglePhoto_ProducesOneBurstOfOne() {
    var t = new DateTime(2024, 6, 1, 10, 0, 0);
    var entries = new[] { this.Entry("IMG_5000.jpg", t) };

    var result = BurstGrouper.GroupBursts(entries);

    Assert.Multiple(() => {
      Assert.That(result, Has.Count.EqualTo(1));
      Assert.That(result[0].Members, Has.Count.EqualTo(1));
      Assert.That(result[0].SuggestedName, Is.EqualTo("IMG_5000"));
    });
  }
}
