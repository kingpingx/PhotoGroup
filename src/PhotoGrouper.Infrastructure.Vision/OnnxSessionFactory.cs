using Microsoft.ML.OnnxRuntime;

namespace PhotoGrouper.Infrastructure.Vision;

/// <summary>
/// Creates inference sessions, preferring the GPU and falling back to the CPU.
/// </summary>
/// <remarks>
/// DirectML is used rather than CUDA because it needs nothing installed beyond the NuGet
/// package and runs on any Direct3D 12 device. CUDA is faster on NVIDIA hardware but requires
/// a matching toolkit and cuDNN on every machine, which is a heavy imposition on a desktop app
/// whose users did not ask to install a compute stack.
///
/// The fallback is not decoration. A machine with no compatible device, a driver that fails to
/// initialise, or a model containing an operator DirectML cannot place will all throw at session
/// creation, and none of those should stop the application working.
/// </remarks>
public sealed class OnnxSessionFactory(bool preferGpu = true)
{
    /// <summary>Whether the last session created actually ran on the GPU.</summary>
    public bool LastSessionUsedGpu { get; private set; }

    /// <summary>Why the GPU was not used, when it was requested but unavailable.</summary>
    public string? GpuUnavailableReason { get; private set; }

    public static bool IsDirectMlAvailable =>
        OrtEnv.Instance().GetAvailableProviders().Any(p => p.Contains("Dml", StringComparison.OrdinalIgnoreCase));

    public InferenceSession Create(string modelPath)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("Model file not found.", modelPath);
        }

        if (preferGpu && IsDirectMlAvailable)
        {
            try
            {
                var session = new InferenceSession(modelPath, CreateOptions(useGpu: true));
                LastSessionUsedGpu = true;
                GpuUnavailableReason = null;
                return session;
            }
            catch (Exception e) when (e is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
            {
                GpuUnavailableReason = e.Message.Split('\n')[0];
            }
        }
        else if (preferGpu)
        {
            GpuUnavailableReason = "No DirectML-capable device was found.";
        }

        LastSessionUsedGpu = false;
        return new InferenceSession(modelPath, CreateOptions(useGpu: false));
    }

    private static SessionOptions CreateOptions(bool useGpu)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };

        if (useGpu)
        {
            // DirectML requires sequential execution; the parallel executor is unsupported and
            // silently degrades or faults depending on the driver.
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AppendExecutionProvider_DML(0);
        }
        else
        {
            // Leave a core for the decode workers. Detection is not the only thing running: the
            // pipeline decodes images in parallel, and letting inference claim every core makes
            // the two starve each other.
            options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount - 1);
        }

        return options;
    }
}
