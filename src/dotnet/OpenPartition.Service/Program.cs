using OpenPartition.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "OpenPartition");
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
