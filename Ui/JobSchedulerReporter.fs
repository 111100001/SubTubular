namespace Ui

open Fabulous.Avalonia
open SubTubular
open type Fabulous.Avalonia.View

module JobReporter =
    type Msg =
        | Updated
        | QueueProgressChanged of float
        | CpuUsageChanged of float
        | GcMemoryPressureChanged of float

    let render (model: JobSchedulerReporter) =
        (VStack(5) {
            ProgressBar(0, 100, float model.CpuUsage, CpuUsageChanged)
                // see https://docs.avaloniaui.net/docs/reference/controls/progressbar#progresstextformat-example
                .progressTextFormat ("CPU usage : {1:0}%")

            ProgressBar(0, 2, float model.GcMemoryPressure, GcMemoryPressureChanged)
                .progressTextFormat ($"GC memory pressure : {model.GcMemoryPressure}")

            ProgressBar(0, float model.All, float model.Completed, QueueProgressChanged)
                .progressTextFormat ($"{model.Queued} queued {model.Running} running {model.Completed} completed")
        })
            .onJobSchedulerReporterUpdated (model, Updated)
