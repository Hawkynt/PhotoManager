using Hawkynt.PhotoManager.Core.Library;

namespace Hawkynt.PhotoManager.Tests.Unit.Library;

[TestFixture]
public class QuickCollectionTests {
  private DirectoryInfo _workingDir = null!;

  [SetUp]
  public void Setup() {
    this._workingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-qc-" + Guid.NewGuid().ToString("N")));
    this._workingDir.Create();
  }

  [TearDown]
  public void Teardown() {
    if (this._workingDir.Exists)
      this._workingDir.Delete(recursive: true);
  }

  private FileInfo CreateFile(string name = "photo.jpg") {
    var f = new FileInfo(Path.Combine(this._workingDir.FullName, name));
    File.WriteAllBytes(f.FullName, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
    f.Refresh();
    return f;
  }

  [Test]
  public void Add_SingleFile_ContainsReturnsTrue() {
    var qc = new QuickCollection();
    var file = this.CreateFile();

    qc.Add(file);

    Assert.That(qc.Contains(file), Is.True);
  }

  [Test]
  public void Remove_AfterAdd_ContainsReturnsFalse() {
    var qc = new QuickCollection();
    var file = this.CreateFile();

    qc.Add(file);
    qc.Remove(file);

    Assert.That(qc.Contains(file), Is.False);
  }

  [Test]
  public void Toggle_FirstCall_AddsAndReturnsTrue() {
    var qc = new QuickCollection();
    var file = this.CreateFile();

    var result = qc.Toggle(file);

    Assert.That(result, Is.True);
    Assert.That(qc.Contains(file), Is.True);
  }

  [Test]
  public void Toggle_SecondCall_RemovesAndReturnsFalse() {
    var qc = new QuickCollection();
    var file = this.CreateFile();

    qc.Toggle(file);
    var result = qc.Toggle(file);

    Assert.That(result, Is.False);
    Assert.That(qc.Contains(file), Is.False);
  }

  [Test]
  public void Clear_EmptiesTheCollection() {
    var qc = new QuickCollection();
    qc.Add(this.CreateFile("a.jpg"));
    qc.Add(this.CreateFile("b.jpg"));
    qc.Add(this.CreateFile("c.jpg"));

    qc.Clear();

    Assert.That(qc.Count, Is.EqualTo(0));
  }

  [Test]
  public void Count_TracksCorrectly() {
    var qc = new QuickCollection();
    Assert.That(qc.Count, Is.EqualTo(0));

    var a = this.CreateFile("a.jpg");
    var b = this.CreateFile("b.jpg");
    qc.Add(a);
    Assert.That(qc.Count, Is.EqualTo(1));

    qc.Add(b);
    Assert.That(qc.Count, Is.EqualTo(2));

    qc.Remove(a);
    Assert.That(qc.Count, Is.EqualTo(1));
  }

  [Test]
  public void DuplicateAdd_IsIdempotent() {
    var qc = new QuickCollection();
    var file = this.CreateFile();

    qc.Add(file);
    qc.Add(file);

    Assert.That(qc.Count, Is.EqualTo(1));
    Assert.That(qc.Contains(file), Is.True);
  }

  [Test]
  public void Contains_ByPath_MatchesCaseInsensitively() {
    var qc = new QuickCollection();
    var file = this.CreateFile();

    qc.Add(file);

    Assert.That(qc.Contains(file.FullName), Is.True);
    Assert.That(qc.Contains(file.FullName.ToUpperInvariant()), Is.True);
  }

  [Test]
  public void GetFiles_ReturnsCurrentPaths() {
    var qc = new QuickCollection();
    var a = this.CreateFile("a.jpg");
    var b = this.CreateFile("b.jpg");

    qc.Add(a);
    qc.Add(b);

    var files = qc.GetFiles();
    Assert.That(files.Count, Is.EqualTo(2));
    Assert.That(files.Contains(a.FullName), Is.True);
    Assert.That(files.Contains(b.FullName), Is.True);
  }

  [Test]
  public void Remove_NonMember_IsNoOp() {
    var qc = new QuickCollection();
    var file = this.CreateFile();

    qc.Remove(file);

    Assert.That(qc.Count, Is.EqualTo(0));
  }

  [Test]
  public void Contains_NullFile_ReturnsFalse() {
    var qc = new QuickCollection();
    Assert.That(qc.Contains((FileInfo)null!), Is.False);
  }

  [Test]
  public void Contains_EmptyPath_ReturnsFalse() {
    var qc = new QuickCollection();
    Assert.That(qc.Contains(string.Empty), Is.False);
  }
}
