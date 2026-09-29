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
        // "localhost" only accepts connections from this computer; "*" listens on every network interface (no authentication!)
        public string ListenAddress {get;set;} = "localhost";
        public int Port {get;set;} = 2382;
        // web page origins allowed to call the server, besides browser extensions and its own pages; "*" allows any
        public List<string> AllowedOrigins {get;set;} = new();
        // browser extensions allowed to call the server, by extension id; empty allows every extension
        public List<string> AllowedExtensions {get;set;} = new();
        // host names the server may be addressed by, besides localhost, IP addresses, names without dots and .local names
        public List<string> AllowedHosts {get;set;} = new();
        // announce the server on the local network over mDNS (only useful with a ListenAddress other than localhost)
        public bool EnableDiscovery {get;set;} = false;
    }
}