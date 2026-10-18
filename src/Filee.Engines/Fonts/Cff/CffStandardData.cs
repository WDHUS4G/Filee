// Fixed name lists from the font specifications: the 391 CFF standard strings and the Standard Encoding (Adobe
// Technical Note #5176, appendices A and B), and the 258 standard Macintosh glyph names of the 'post' table.

namespace Filee.Engines.Fonts.Cff;

/// <summary>Standard strings and encodings shared by every CFF font, and the standard 'post' glyph names.</summary>
internal static class CffStandardData
{
    /// <summary>CFF strings with SID 0–390; custom strings in the String INDEX start at SID 391.</summary>
    public static readonly string[] StandardStrings = (
        ".notdef space exclam quotedbl numbersign dollar percent ampersand quoteright parenleft parenright asterisk plus " +
        "comma hyphen period slash zero one two three four five six seven eight nine colon semicolon less equal greater " +
        "question at A B C D E F G H I J K L M N O P Q R S T U V W X Y Z bracketleft backslash bracketright asciicircum " +
        "underscore quoteleft a b c d e f g h i j k l m n o p q r s t u v w x y z braceleft bar braceright asciitilde " +
        "exclamdown cent sterling fraction yen florin section currency quotesingle quotedblleft guillemotleft " +
        "guilsinglleft guilsinglright fi fl endash dagger daggerdbl periodcentered paragraph bullet quotesinglbase " +
        "quotedblbase quotedblright guillemotright ellipsis perthousand questiondown grave acute circumflex tilde macron " +
        "breve dotaccent dieresis ring cedilla hungarumlaut ogonek caron emdash AE ordfeminine Lslash Oslash OE " +
        "ordmasculine ae dotlessi lslash oslash oe germandbls onesuperior logicalnot mu trademark Eth onehalf plusminus " +
        "Thorn onequarter divide brokenbar degree thorn threequarters twosuperior registered minus eth multiply " +
        "threesuperior copyright Aacute Acircumflex Adieresis Agrave Aring Atilde Ccedilla Eacute Ecircumflex Edieresis " +
        "Egrave Iacute Icircumflex Idieresis Igrave Ntilde Oacute Ocircumflex Odieresis Ograve Otilde Scaron Uacute " +
        "Ucircumflex Udieresis Ugrave Yacute Ydieresis Zcaron aacute acircumflex adieresis agrave aring atilde ccedilla " +
        "eacute ecircumflex edieresis egrave iacute icircumflex idieresis igrave ntilde oacute ocircumflex odieresis " +
        "ograve otilde scaron uacute ucircumflex udieresis ugrave yacute ydieresis zcaron exclamsmall Hungarumlautsmall " +
        "dollaroldstyle dollarsuperior ampersandsmall Acutesmall parenleftsuperior parenrightsuperior twodotenleader " +
        "onedotenleader zerooldstyle oneoldstyle twooldstyle threeoldstyle fouroldstyle fiveoldstyle sixoldstyle " +
        "sevenoldstyle eightoldstyle nineoldstyle commasuperior threequartersemdash periodsuperior questionsmall " +
        "asuperior bsuperior centsuperior dsuperior esuperior isuperior lsuperior msuperior nsuperior osuperior rsuperior " +
        "ssuperior tsuperior ff ffi ffl parenleftinferior parenrightinferior Circumflexsmall hyphensuperior Gravesmall " +
        "Asmall Bsmall Csmall Dsmall Esmall Fsmall Gsmall Hsmall Ismall Jsmall Ksmall Lsmall Msmall Nsmall Osmall Psmall " +
        "Qsmall Rsmall Ssmall Tsmall Usmall Vsmall Wsmall Xsmall Ysmall Zsmall colonmonetary onefitted rupiah Tildesmall " +
        "exclamdownsmall centoldstyle Lslashsmall Scaronsmall Zcaronsmall Dieresissmall Brevesmall Caronsmall " +
        "Dotaccentsmall Macronsmall figuredash hypheninferior Ogoneksmall Ringsmall Cedillasmall questiondownsmall " +
        "oneeighth threeeighths fiveeighths seveneighths onethird twothirds zerosuperior foursuperior fivesuperior " +
        "sixsuperior sevensuperior eightsuperior ninesuperior zeroinferior oneinferior twoinferior threeinferior " +
        "fourinferior fiveinferior sixinferior seveninferior eightinferior nineinferior centinferior dollarinferior " +
        "periodinferior commainferior Agravesmall Aacutesmall Acircumflexsmall Atildesmall Adieresissmall Aringsmall " +
        "AEsmall Ccedillasmall Egravesmall Eacutesmall Ecircumflexsmall Edieresissmall Igravesmall Iacutesmall " +
        "Icircumflexsmall Idieresissmall Ethsmall Ntildesmall Ogravesmall Oacutesmall Ocircumflexsmall Otildesmall " +
        "Odieresissmall OEsmall Oslashsmall Ugravesmall Uacutesmall Ucircumflexsmall Udieresissmall Yacutesmall " +
        "Thornsmall Ydieresissmall 001.000 001.001 001.002 001.003 Black Bold Book Light Medium Regular Roman Semibold")
        .Split(' ');

    /// <summary>
    /// Standard Encoding as code → SID (0 = not encoded). Used by the deprecated <c>seac</c> form of
    /// <c>endchar</c>, whose base and accent are given as Standard Encoding codes.
    /// </summary>
    public static readonly ushort[] StandardEncoding = BuildStandardEncoding();

    /// <summary>The 258 glyph names a 'post' format 2 table can refer to by index.</summary>
    public static readonly string[] MacGlyphNames = (
        ".notdef .null nonmarkingreturn space exclam quotedbl numbersign dollar percent ampersand quotesingle parenleft " +
        "parenright asterisk plus comma hyphen period slash zero one two three four five six seven eight nine colon " +
        "semicolon less equal greater question at A B C D E F G H I J K L M N O P Q R S T U V W X Y Z bracketleft " +
        "backslash bracketright asciicircum underscore grave a b c d e f g h i j k l m n o p q r s t u v w x y z " +
        "braceleft bar braceright asciitilde Adieresis Aring Ccedilla Eacute Ntilde Odieresis Udieresis aacute agrave " +
        "acircumflex adieresis atilde aring ccedilla eacute egrave ecircumflex edieresis iacute igrave icircumflex " +
        "idieresis ntilde oacute ograve ocircumflex odieresis otilde uacute ugrave ucircumflex udieresis dagger degree " +
        "cent sterling section bullet paragraph germandbls registered copyright trademark acute dieresis notequal AE " +
        "Oslash infinity plusminus lessequal greaterequal yen mu partialdiff summation product pi integral ordfeminine " +
        "ordmasculine Omega ae oslash questiondown exclamdown logicalnot radical florin approxequal Delta guillemotleft " +
        "guillemotright ellipsis nonbreakingspace Agrave Atilde Otilde OE oe endash emdash quotedblleft quotedblright " +
        "quoteleft quoteright divide lozenge ydieresis Ydieresis fraction currency guilsinglleft guilsinglright fi fl " +
        "daggerdbl periodcentered quotesinglbase quotedblbase perthousand Acircumflex Ecircumflex Aacute Edieresis Egrave " +
        "Iacute Icircumflex Idieresis Igrave Oacute Ocircumflex apple Ograve Uacute Ucircumflex Ugrave dotlessi " +
        "circumflex tilde macron breve dotaccent ring cedilla hungarumlaut ogonek caron Lslash lslash Scaron scaron " +
        "Zcaron zcaron brokenbar Eth eth Yacute yacute Thorn thorn minus multiply onesuperior twosuperior threesuperior " +
        "onehalf onequarter threequarters franc Gbreve gbreve Idotaccent Scedilla scedilla Cacute cacute Ccaron ccaron " +
        "dcroat")
        .Split(' ');

    private static ushort[] BuildStandardEncoding()
    {
        var map = new ushort[256];
        // Printable ASCII maps to SIDs 1–95 in order.
        for (var code = 32; code <= 126; code++)
            map[code] = (ushort)(code - 31);
        // The upper half, as (first code, first SID, count) runs.
        (int Code, int Sid, int Count)[] runs =
        [
            (161, 96, 15), (177, 111, 4), (182, 115, 8), (191, 123, 1), (193, 124, 8), (202, 132, 2), (205, 134, 4),
            (225, 138, 1), (227, 139, 1), (232, 140, 4), (241, 144, 1), (245, 145, 1), (248, 146, 4),
        ];
        foreach (var (code, sid, count) in runs)
        {
            for (var i = 0; i < count; i++)
                map[code + i] = (ushort)(sid + i);
        }
        return map;
    }
}
