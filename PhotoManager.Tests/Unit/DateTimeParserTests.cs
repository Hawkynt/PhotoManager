using PhotoManager.Core.Models;
using PhotoManager.Core.Services;

namespace PhotoManager.Tests.Unit;

[TestFixture]
public class DateTimeParserTests {
  private DateTimeParser _parser;
  private ImportSettings _settings;

  [SetUp]
  public void Setup() {
    this._parser = new DateTimeParser();
    this._settings = new ImportSettings {
      MinimumValidDate = new DateTime(1990, 1, 1)
    };
  }

  [Test]
  [TestCase("IMG_20240115_143022.jpg", "2024-01-15 14:30:22")]
  [TestCase("2023-12-25-18-45-30.png", "2023-12-25 18:45:30")]
  [TestCase("photo_2023_06_15_09_30_45.jpg", "2023-06-15 09:30:45")]
  [TestCase("20220815093045.jpg", "2022-08-15 09:30:45")]
  public async Task ParseDateFromFileName_ValidFormats_ExtractsCorrectDate(string fileName, string expectedDate) {
    // Arrange
    var fileInfo = new FileInfo(Path.Combine(Path.GetTempPath(), fileName));
    var fileToImport = new FileToImport(fileInfo);
    var expected = DateTime.Parse(expectedDate);

    // Act
    var dates = new List<DateTime>();
    await foreach (var date in this._parser.ParseDateFromFileName(fileToImport, this._settings))
      dates.Add(date);

    // Assert
    Assert.That(dates, Has.Member(expected));
  }

  [Test]
  public async Task ParseDateFromFileName_NoDateInFileName_ReturnsEmpty() {
    // Arrange
    var fileInfo = new FileInfo(Path.Combine(Path.GetTempPath(), "random_photo.jpg"));
    var fileToImport = new FileToImport(fileInfo);

    // Act
    var dates = new List<DateTime>();
    await foreach (var date in this._parser.ParseDateFromFileName(fileToImport, this._settings))
      dates.Add(date);

    // Assert
    Assert.That(dates, Is.Empty);
  }

  [Test]
  public void GetSupportedDateFormats_ReturnsFormats() {
    // Act
    var formats = this._parser.GetSupportedDateFormats();

    // Assert
    Assert.That(formats, Is.Not.Null);
    Assert.That(formats.Length, Is.GreaterThan(0));
    Assert.That(formats, Contains.Item("yyyyMMddHHmmss"));
    Assert.That(formats, Contains.Item("yyyy-MM-dd"));
  }

  [Test]
  [TestCase("991225123045.jpg", 1999)]
  [TestCase("001225123045.jpg", 2000)]
  [TestCase("491225123045.jpg", 2049)]
  [TestCase("501225123045.jpg", 1950)]
  public async Task ParseDateFromFileName_TwoDigitYear_CorrectCentury(string fileName, int expectedYear) {
    // Arrange
    var fileInfo = new FileInfo(Path.Combine(Path.GetTempPath(), fileName));
    var fileToImport = new FileToImport(fileInfo);

    // Act
    var dates = new List<DateTime>();
    await foreach (var date in this._parser.ParseDateFromFileName(fileToImport, this._settings))
      dates.Add(date);

    // Assert
    Assert.That(dates, Is.Not.Empty);
    Assert.That(dates.First().Year, Is.EqualTo(expectedYear));
  }
}
