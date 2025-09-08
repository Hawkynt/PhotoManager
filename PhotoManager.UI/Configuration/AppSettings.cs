namespace PhotoManager.UI.Configuration;

public class AppSettings {
  public string DefaultDatePattern { get; set; } = "yyyy/yyyyMMdd/HHmmss";
  public DateTime MinimumValidDate { get; set; } = new(1990, 1, 1);
  public int MaxParallelism { get; set; } = Environment.ProcessorCount;
  public string DefaultLanguage { get; set; } = "en-US";
}

public class UserSettings {
  public string LastSourceDirectory { get; set; } = string.Empty;
  public string LastDestinationDirectory { get; set; } = string.Empty;
  public bool PreserveOriginals { get; set; }
  public bool RecursiveSearch { get; set; } = true;
}
