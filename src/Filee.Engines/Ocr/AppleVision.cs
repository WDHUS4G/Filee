// Text recognition with Apple's Vision framework (VNRecognizeTextRequest), part of macOS: nothing to download, and
// it runs on the Neural Engine or GPU. Called through the Objective-C runtime (objc_msgSend) so Filee needs no
// bridging library; the framework is loaded only on macOS, the first time it is asked for.
//
// A CGRect (four doubles, 32 bytes) comes back in registers on Apple silicon but through memory on Intel Macs, where
// it needs objc_msgSend_stret. Objects Filee creates with alloc/init are released, the ones Vision hands back are
// autoreleased and go with the autorelease pool of each call.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Filee.Engines.Ocr;

/// <summary>Reads the text of a picture: what <see cref="VisionConverter"/> needs from Apple Vision.</summary>
internal interface ITextRecognizer
{
    /// <summary>True when the recognizer can run on this computer.</summary>
    bool IsAvailable { get; }

    /// <summary>The languages it reads, as Vision identifiers (<c>en-US</c>, <c>zh-Hans</c>).</summary>
    IReadOnlyCollection<string> Languages { get; }

    /// <summary>The lines of text in <paramref name="picture"/>, in <paramref name="languages"/> (most likely first).</summary>
    IReadOnlyList<OcrLine> Recognize(string picture, IReadOnlyList<string> languages, CancellationToken cancellationToken);
}

/// <summary>Apple Vision's text recognition, accurate level, newest revision of this macOS.</summary>
[SupportedOSPlatform("macos")]
internal sealed class AppleVision : ITextRecognizer
{
    private const string Vision = "/System/Library/Frameworks/Vision.framework/Vision";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8
    private const nint Accurate = 0; // VNRequestTextRecognitionLevelAccurate

    private static readonly Lazy<bool> Loaded = new(() =>
        NativeLibrary.TryLoad(Vision, out _) && objc_getClass("VNRecognizeTextRequest") != 0);

    private static readonly Lazy<IReadOnlyCollection<string>> Supported = new(ReadLanguages);

    public bool IsAvailable => Loaded.Value;

    public IReadOnlyCollection<string> Languages => IsAvailable ? Supported.Value : [];

    public IReadOnlyList<OcrLine> Recognize(string picture, IReadOnlyList<string> languages, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            throw new PlatformNotSupportedException("Apple Vision text recognition is not available.");
        var pool = New("NSAutoreleasePool");
        nint request = 0;
        nint handler = 0;
        var path = CFStringCreateWithCString(0, picture, Utf8);
        try
        {
            request = NewRequest();
            SendVoid(request, Sel("setUsesLanguageCorrection:"), 1);
            var wanted = Send(objc_getClass("NSMutableArray"), Sel("array"));
            foreach (var language in languages)
            {
                var name = CFStringCreateWithCString(0, language, Utf8);
                Send(wanted, Sel("addObject:"), name);
                CFRelease(name);
            }
            Send(request, Sel("setRecognitionLanguages:"), wanted);

            var url = Send(objc_getClass("NSURL"), Sel("fileURLWithPath:"), path);
            var options = Send(objc_getClass("NSDictionary"), Sel("dictionary"));
            handler = Send(Send(objc_getClass("VNImageRequestHandler"), Sel("alloc")), Sel("initWithURL:options:"), url, options);
            var requests = Send(objc_getClass("NSArray"), Sel("arrayWithObject:"), request);
            bool done;
            nint error;
            using (cancellationToken.Register(() => Send(request, Sel("cancel"))))
                done = Perform(handler, Sel("performRequests:error:"), requests, out error);
            cancellationToken.ThrowIfCancellationRequested();
            if (!done)
                throw new InvalidOperationException($"Apple Vision couldn't read the picture. {Describe(error)}".Trim());
            return Lines(Send(request, Sel("results")));
        }
        finally
        {
            if (handler != 0)
                Send(handler, Sel("release"));
            if (request != 0)
                Send(request, Sel("release"));
            if (path != 0)
                CFRelease(path);
            Send(pool, Sel("drain"));
        }
    }

    /// <summary>The recognized lines, each with a box per word (estimated from the line when Vision gives none).</summary>
    private static List<OcrLine> Lines(nint observations)
    {
        var lines = new List<OcrLine>();
        var count = observations == 0 ? 0 : (long)Send(observations, Sel("count"));
        for (long i = 0; i < count; i++)
        {
            var observation = Send(observations, Sel("objectAtIndex:"), (nint)i);
            var candidates = Send(observation, Sel("topCandidates:"), 1);
            if (candidates == 0 || Send(candidates, Sel("count")) == 0)
                continue;
            var candidate = Send(candidates, Sel("objectAtIndex:"), 0);
            var text = ToText(Send(candidate, Sel("string")));
            if (string.IsNullOrWhiteSpace(text))
                continue;
            var box = Rect(observation, Sel("boundingBox"));
            var estimate = OcrLine.Estimate(text, box.X, box.Y, box.Width, box.Height);
            var words = new List<OcrWord>();
            var ranges = OcrLine.WordRanges(text);
            for (var w = 0; w < ranges.Count; w++)
            {
                var (start, length) = ranges[w];
                // NSString ranges count UTF-16 units, as .NET strings do.
                var wordBox = SendRange(candidate, Sel("boundingBoxForRange:error:"), new NSRange((nuint)start, (nuint)length), out _);
                if (wordBox == 0)
                {
                    words.Add(estimate.Words[w]);
                    continue;
                }
                var rect = Rect(wordBox, Sel("boundingBox"));
                words.Add(new OcrWord(text.Substring(start, length), rect.X, rect.Width));
            }
            lines.Add(new OcrLine(box.X, box.Y, box.Width, box.Height, words));
        }
        return lines;
    }

    /// <summary>A request at the accurate level and the newest revision this macOS has (owned: release it).</summary>
    private static nint NewRequest()
    {
        var type = objc_getClass("VNRecognizeTextRequest");
        var request = Send(Send(type, Sel("alloc")), Sel("init"));
        Send(request, Sel("setRecognitionLevel:"), Accurate);
        // Without it the revision depends on the SDK the program was built with, which for .NET's host may be old.
        var revisions = Send(type, Sel("supportedRevisions"));
        if (revisions != 0)
        {
            var newest = Send(revisions, Sel("lastIndex"));
            if (newest != nint.MaxValue) // NSNotFound
                Send(request, Sel("setRevision:"), newest);
        }
        return request;
    }

    private static IReadOnlyCollection<string> ReadLanguages()
    {
        var pool = New("NSAutoreleasePool");
        var request = NewRequest();
        try
        {
            var list = SendWithError(request, Sel("supportedRecognitionLanguagesAndReturnError:"), out _);
            if (list == 0)
                return VisionLanguages.Fallback;
            var languages = new List<string>();
            var count = (long)Send(list, Sel("count"));
            for (long i = 0; i < count; i++)
            {
                if (ToText(Send(list, Sel("objectAtIndex:"), (nint)i)) is { Length: > 0 } language)
                    languages.Add(language);
            }
            return languages.Count > 0 ? languages : VisionLanguages.Fallback;
        }
        finally
        {
            Send(request, Sel("release"));
            Send(pool, Sel("drain"));
        }
    }

    private static string? Describe(nint error) =>
        error == 0 ? null : ToText(Send(error, Sel("localizedDescription")));

    private static string? ToText(nint nsString) =>
        nsString == 0 ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));

    private static nint New(string type) => Send(Send(objc_getClass(type), Sel("alloc")), Sel("init"));

    private static nint Sel(string name) => sel_registerName(name);

    private static CGRect Rect(nint receiver, nint selector)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            SendRectStret(out var rect, receiver, selector);
            return rect;
        }
        return SendRect(receiver, selector);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NSRange(nuint location, nuint length)
    {
        public readonly nuint Location = location;
        public readonly nuint Length = length;
    }

    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint value);

    [DllImport(ObjC)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint first, nint second);

    /// <summary>A method with one BOOL argument and no result.</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector, byte value);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendWithError(nint receiver, nint selector, out nint error);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendRange(nint receiver, nint selector, NSRange range, out nint error);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool Perform(nint receiver, nint selector, nint requests, out nint error);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern CGRect SendRect(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")]
    private static extern void SendRectStret(out CGRect result, nint receiver, nint selector);
}
