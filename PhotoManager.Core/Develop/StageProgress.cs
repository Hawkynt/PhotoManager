namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// Progress event emitted by long-running restoration stages so the UI
/// can show an accurate "patch X / Y · ~M:SS remaining" instead of a
/// pure stopwatch readout.
///
/// <para><see cref="StageName"/> identifies the stage (so the UI can
/// label "Denoise" / "Upscale (1/2)" / etc.); <see cref="DoneUnits"/>
/// over <see cref="TotalUnits"/> is the within-stage fraction.
/// Tile-based stages report tile counts; non-tile stages can report
/// (0,1) at start and (1,1) at completion so the UI still gets a
/// stage transition signal.</para>
///
/// <para><see cref="EstimatedTotalSeconds"/> is a conservative time
/// estimate the stage emits when it knows roughly how long it'll
/// take — usually computed from input pixel count and a known
/// per-stage throughput rate. The UI uses it as the initial ETA before
/// the first tile completes (= before the actual elapsed/done ratio
/// becomes meaningful). Set to 0 when the stage has no estimate to
/// offer; the UI then falls back to per-key history.</para>
/// </summary>
public readonly record struct StageProgress(
  string StageName,
  int DoneUnits,
  int TotalUnits,
  double EstimatedTotalSeconds = 0.0,
  double EstimatedRemainingPipelineSeconds = 0.0
);
