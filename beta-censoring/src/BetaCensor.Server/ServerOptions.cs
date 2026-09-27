namespace BetaCensor.Server
{
    public class ServerOptions
    {
        public int WorkerCount {get;set;} = 2;
        public bool EnableSignalR {get;set;} = true;
        public bool EnableRest {get;set;} = true;
        public string? SocketPath {get;set;}
        public string? ImageDimensions {get;set;} = null;
        public CensorCore.OptimizationMode OptimizationMode {get;set;} = CensorCore.OptimizationMode.Normal;
        // GPU inference: CUDA on Linux (needs the GPU build), DirectML on Windows
        public bool UseGpu {get;set;} = false;
        public int GpuDeviceId {get;set;} = 0;
        // caps the model's GPU memory arena; unset means no limit (requests fail when it's exceeded)
        public int? GpuMemoryLimitMB {get;set;}
        // dedicated threads running the model on the GPU (CUDA keeps GPU memory for each thread that uses it); 0 = thread pool.
        // More than one simultaneous run gave no speed-up on an RTX 3060 Ti and eventually corrupted the CUDA context.
        public int GpuMaxConcurrentRuns {get;set;} = 1;
        public bool EnableLargeMessages = true;
    }
}