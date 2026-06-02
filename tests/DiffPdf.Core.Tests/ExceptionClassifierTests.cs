using System.Net.Sockets;
using DiffPdf.Messaging;

namespace DiffPdf.Core.Tests;

public class ExceptionClassifierTests
{
    [Fact]
    public void IoAndTimeout_AreTransient()
    {
        Assert.True(ExceptionClassifier.IsTransient(new IOException("disk blip")));
        Assert.True(ExceptionClassifier.IsTransient(new TimeoutException()));
        Assert.True(ExceptionClassifier.IsTransient(new SocketException()));
    }

    [Fact]
    public void Transient_DetectedThroughInnerException()
    {
        var wrapped = new InvalidOperationException("outer", new TimeoutException());
        Assert.True(ExceptionClassifier.IsTransient(wrapped));
    }

    [Fact]
    public void BadRequestErrors_ArePermanent()
    {
        Assert.False(ExceptionClassifier.IsTransient(new DirectoryNotFoundException("/missing")));
        Assert.False(ExceptionClassifier.IsTransient(new ArgumentException("bad scope")));
    }
}
