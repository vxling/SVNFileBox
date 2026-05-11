#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Serilog;

namespace SVNFileBox.Services;

/// <summary>
/// Creates new files of various types with minimal valid content.
/// Uses the same approach as Windows Shell New menu (approach B).
/// </summary>
public static class NewFileService
{
    // ─── Minimal valid file bytes ────────────────────────────────────────────

    // .docx = ZIP containing [Content_Types].xml + word/document.xml + _rels/.rels
    private static byte[] CreateDocx()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // [Content_Types].xml
            WriteEntry(zip, "[Content_Types].xml",
                @"<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">" +
                @"<Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>" +
                @"<Default Extension=""xml"" ContentType=""application/xml""/>" +
                @"<Override PartName=""/word/document.xml"" ContentType=""application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml""/>" +
                @"</Types>");

            // _rels/.rels
            WriteEntry(zip, "_rels/.rels",
                @"<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">" +
                @"<Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""word/document.xml""/>" +
                @"</Relationships>");

            // word/document.xml (minimal valid document)
            WriteEntry(zip, "word/document.xml",
                @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>" +
                @"<w:document xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">" +
                @"<w:body><w:p><w:pPr/><w:r><w:t></w:t></w:r></w:p></w:body></w:document>");
        }
        return ms.ToArray();
    }

    // .xlsx = ZIP containing [Content_Types].xml + xl/workbook.xml + xl/worksheets/sheet1.xml + _rels/.rels
    private static byte[] CreateXlsx()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml",
                @"<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">" +
                @"<Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>" +
                @"<Default Extension=""xml"" ContentType=""application/xml""/>" +
                @"<Override PartName=""/xl/workbook.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml""/>" +
                @"<Override PartName=""/xl/worksheets/sheet1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml""/>" +
                @"</Types>");

            WriteEntry(zip, "_rels/.rels",
                @"<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">" +
                @"<Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""xl/workbook.xml""/>" +
                @"</Relationships>");

            WriteEntry(zip, "xl/workbook.xml",
                @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>" +
                @"<workbook xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">" +
                @"<sheets><sheet name=""Sheet1"" sheetId=""1"" r:id=""rId1"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships""/></sheets></workbook>");

            WriteEntry(zip, "xl/worksheets/sheet1.xml",
                @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>" +
                @"<worksheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">" +
                @"<sheetData><row r=""1""><c r=""A1"" t=""str""><v></v></c></row></sheetData></worksheet>");

            WriteEntry(zip, "xl/_rels/workbook.xml.rels",
                @"<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">" +
                @"<Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"" Target=""worksheets/sheet1.xml""/>" +
                @"</Relationships>");
        }
        return ms.ToArray();
    }

    // .pptx = ZIP containing minimal presentation
    private static byte[] CreatePptx()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml",
                @"<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">" +
                @"<Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>" +
                @"<Default Extension=""xml"" ContentType=""application/xml""/>" +
                @"<Override PartName=""/ppt/presentation.xml"" ContentType=""application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml""/>" +
                @"<Override PartName=""/ppt/slides/slide1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.presentationml.slide+xml""/>" +
                @"</Types>");

            WriteEntry(zip, "_rels/.rels",
                @"<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">" +
                @"<Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""ppt/presentation.xml""/>" +
                @"</Relationships>");

            WriteEntry(zip, "ppt/presentation.xml",
                @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>" +
                @"<p:presentation xmlns:p=""http://schemas.openxmlformats.org/presentationml/2006/main"">" +
                @"<p:sldIdLst><p:sldId id=""256"" r:id=""rId1"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships""/></p:sldIdLst>" +
                @"<p:sldSz cx=""9144000"" cy=""6858000""/></p:presentation>");

            WriteEntry(zip, "ppt/slides/slide1.xml",
                @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>" +
                @"<p:sld xmlns:p=""http://schemas.openxmlformats.org/presentationml/2006/main"">" +
                @"<p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=""1"" name=""""/>" +
                @"<p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
                @"<p:grpSpPr><a:xfrm xmlns:a=""http://schemas.openxmlformats.org/drawingml/2006/main"">" +
                @"<a:off x=""0"" y=""0""/><a:ext cx=""0"" cy=""0""/><a:chOff x=""0"" y=""0""/><a:chExt cx=""0"" cy=""0""/>" +
                @"</a:xfrm></p:grpSpPr></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");

            WriteEntry(zip, "ppt/_rels/presentation.xml.rels",
                @"<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">" +
                @"<Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide"" Target=""slides/slide1.xml""/>" +
                @"</Relationships>");
        }
        return ms.ToArray();
    }

    // ─── PNG: 1x1 transparent pixel ─────────────────────────────────────────────
    private static byte[] CreatePng()
    {
        // Minimal valid PNG: signature + IHDR + IDAT + IEND
        var signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        // IHDR chunk: 1x1 px, 8-bit RGB
        var ihdr = MakeChunk("IHDR", new byte[] { 0, 0, 0, 1,   // width=1
                                                   0, 0, 0, 1,   // height=1
                                                   8,            // bit depth=8
                                                   2,            // color type=2 (RGB)
                                                   0,            // compression=deflate
                                                   0,            // filter=standard
                                                   0 });         // interlace=none

        // IDAT chunk: single transparent pixel (R=0,G=0,B=0), deflate
        var idatData = new byte[] { 0x08, 0x15, 0x01, 0x00, 0x00, 0xFF, 0xFF, 0x00 }; // filter-byte + zlib wrapper for 3 RGB bytes
        // Simpler: raw scanline for 1x1 RGB with filter=none
        var raw = new byte[] { 0, 0, 0, 0 }; // filter=none + R,G,B = 0,0,0 (black pixel)
        var idatCompressed = Deflate(raw);
        var idat = MakeChunk("IDAT", idatCompressed);

        // IEND chunk
        var iend = MakeChunk("IEND", Array.Empty<byte>());

        var result = new byte[signature.Length + ihdr.Length + idat.Length + iend.Length];
        int offset = 0;
        Buffer.BlockCopy(signature, 0, result, offset, signature.Length); offset += signature.Length;
        Buffer.BlockCopy(ihdr, 0, result, offset, ihdr.Length); offset += ihdr.Length;
        Buffer.BlockCopy(idat, 0, result, offset, idat.Length); offset += idat.Length;
        Buffer.BlockCopy(iend, 0, result, iend.Length, iend.Length);
        return result;
    }

    private static byte[] MakeChunk(string type, byte[] data)
    {
        var chunk = new byte[data.Length + 12];
        // length (big-endian)
        chunk[0] = (byte)(data.Length >> 24);
        chunk[1] = (byte)(data.Length >> 16);
        chunk[2] = (byte)(data.Length >> 8);
        chunk[3] = (byte)data.Length;
        // type
        for (int i = 0; i < 4; i++) chunk[4 + i] = (byte)type[i];
        // data
        Buffer.BlockCopy(data, 0, chunk, 8, data.Length);
        // CRC32 of type+data
        var crc = Crc32(type + Encoding.ASCII.GetString(data));
        chunk[^4] = (byte)(crc >> 24);
        chunk[^3] = (byte)(crc >> 16);
        chunk[^2] = (byte)(crc >> 8);
        chunk[^1] = (byte)crc;
        return chunk;
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var ms = new MemoryStream();
        // zlib header + raw deflate (no compression) + adler32
        ms.WriteByte(0x78); // CMF
        ms.WriteByte(0x01); // FLG
        // deflate block: BFINAL=1, BTYPE=01 (fixed Huffman), LEN=0, NLEN=~LEN
        int len = raw.Length;
        ms.WriteByte(0x01); // BFINAL+BTYPE=0xC1
        ms.WriteByte((byte)(len & 0xFF));
        ms.WriteByte((byte)((len >> 8) & 0xFF));
        ms.WriteByte((byte)((~len) & 0xFF));
        ms.WriteByte((byte)(((~len) >> 8) & 0xFF));
        ms.Write(raw, 0, raw.Length);
        // adler32
        uint a = 1, b = 0;
        foreach (byte x in raw) { a = (a + x) % 65521; b = (b + a) % 65521; }
        uint adler = (b << 16) | a;
        ms.WriteByte((byte)(adler >> 24));
        ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8));
        ms.WriteByte((byte)adler);
        return ms.ToArray();
    }

    // CRC32 for PNG chunks (standard polynomial 0xEDB88320)
    private static uint Crc32(string data)
    {
        var bytes = Encoding.ASCII.GetBytes(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte x in bytes)
        {
            int idx = (int)((crc ^ x) & 0xFF);
            uint c = _crcTable[idx];
            crc = (crc >> 8) ^ c;
        }
        return crc ^ 0xFFFFFFFF;
    }
    private static readonly uint[] _crcTable = new uint[256];

    static NewFileService()
    {
        for (int i = 0; i < 256; i++)
        {
            uint c = (uint)i;
            for (int j = 0; j < 8; j++)
                c = (c & 1) != 0 ? (0xEDB88320 ^ (c >> 1)) : (c >> 1);
            _crcTable[i] = c;
        }
    }

    // ─── BMP: 1x1 white pixel ─────────────────────────────────────────────────
    private static byte[] CreateBmp()
    {
        // BMP header (14 bytes) + DIB header (40 bytes) + pixel data (3 bytes)
        var bmp = new byte[14 + 40 + 12]; // 14 header + 40 DIB + 2x2 RGB pixels + padding
        // BMP File Header
        bmp[0] = 0x42; bmp[1] = 0x4D; // "BM"
        int fileSize = 14 + 40 + 12;
        bmp[2] = (byte)fileSize; bmp[3] = (byte)(fileSize >> 8);
        bmp[4] = 0; bmp[5] = 0; // reserved
        bmp[6] = 0; bmp[7] = 0;
        bmp[8] = 0; bmp[9] = 0;
        bmp[10] = 0x36; bmp[11] = 0; bmp[12] = 0; bmp[13] = 0; // pixel offset=54

        // DIB Header (BITMAPINFOHEADER, 40 bytes)
        bmp[14] = 40; // header size
        bmp[18] = 1; bmp[19] = 0; // width=1
        bmp[22] = 1; bmp[23] = 0; // height=1 (positive=bottom-up)
        bmp[24] = 1; bmp[25] = 0; // color planes=1
        bmp[26] = 24; // bits per pixel=24 (BGR)
        bmp[30] = 0; bmp[34] = 0; bmp[38] = 0; bmp[42] = 0; // image size / x/y ppm = 0

        // Pixel data: 1 white pixel (BGR=FF,FF,FF) + 3 byte padding per row
        int pixelOffset = 14 + 40;
        bmp[pixelOffset] = 0xFF; bmp[pixelOffset + 1] = 0xFF; bmp[pixelOffset + 2] = 0xFF;
        return bmp;
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(content);
    }

    // ─── Public API ──────────────────────────────────────────────────────────

    public static bool Create(string fullPath)
    {
        try
        {
            var ext = Path.GetExtension(fullPath).ToLowerInvariant();
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            byte[] bytes = ext switch
            {
                ".txt" or ".rtf" => Array.Empty<byte>(),
                ".docx" => CreateDocx(),
                ".xlsx" => CreateXlsx(),
                ".pptx" => CreatePptx(),
                ".png" => CreatePng(),
                ".bmp" => CreateBmp(),
                _ => Array.Empty<byte>()
            };

            File.WriteAllBytes(fullPath, bytes);
            Log.Information("Created new file: {Path}", fullPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create file: {Path}", fullPath);
            return false;
        }
    }
}
