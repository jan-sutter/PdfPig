﻿namespace UglyToad.PdfPig.Writer
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Content;
    using Core;
    using Fonts;
    using Graphics.Colors;
    using Graphics.Operations;
    using Graphics.Operations.General;
    using Graphics.Operations.PathConstruction;
    using Graphics.Operations.SpecialGraphicsState;
    using Graphics.Operations.TextObjects;
    using Graphics.Operations.TextPositioning;
    using Graphics.Operations.TextShowing;
    using Graphics.Operations.TextState;
    using Images;
    using PdfFonts;
    using Tokens;
    using Graphics.Operations.PathPainting;
    using Images.Png;
    using Actions;

    internal class NameConflictSolver
    {
        private readonly string prefix;
        private int key = 0;
        private readonly HashSet<string> xobjectNamesUsed = new HashSet<string>();

        public NameConflictSolver(string prefix)
        {
            this.prefix = prefix;
        }

        private string ExtractPrefix(string? name)
        {
            if (name is null)
            {
                return prefix;
            }

            var i = 0;
            while (i < name.Length && (name[i] < '0' || name[i] > '9'))
            {
                i++;
            }

            return i != 0 ? name.Substring(0, i) : prefix;
        }

        public string NewName(string? originalName = null)
        {
            var newPrefix = ExtractPrefix(originalName);

            var name = $"{newPrefix}{key}";

            while (xobjectNamesUsed.Contains(name))
            {
                name = $"{newPrefix}{++key}";
            }

            xobjectNamesUsed.Add(name);

            return name;
        }

        public string FixName(string name)
        {
            if (xobjectNamesUsed.Contains(name))
            {
                return NewName(name);
            }

            xobjectNamesUsed.Add(name);
            return name;
        }
    }

    /// <summary>
    /// A builder used to add construct a page in a PDF document.
    /// </summary>
    public class PdfPageBuilder
    {
        // parent
        private readonly PdfDocumentBuilder documentBuilder;

        // all page data other than content streams
        internal readonly Dictionary<NameToken, IToken> pageDictionary = new Dictionary<NameToken, IToken>();

        // streams
        internal readonly List<IPageContentStream> contentStreams;
        private IPageContentStream currentStream;

        // links to be resolved when all page references are available
        internal readonly List<(DictionaryToken token, PdfAction action)>? links;

        // maps fonts added using PdfDocumentBuilder to page font names
        private readonly Dictionary<Guid, NameToken> documentFonts = new Dictionary<Guid, NameToken>();
        internal int nextFontId = 1;

        //a sequence number of ShowText operation to determine whether letters belong to same operation or not (letters that belong to different operations have less changes to belong to same word)
        private int textSequence;

        private NameConflictSolver xobjectsNames = new NameConflictSolver("I");

        private NameConflictSolver gStateNames = new NameConflictSolver("GS");

        internal int? rotation;

        internal IReadOnlyDictionary<string, IToken> Resources => pageDictionary.GetOrCreateDict(NameToken.Resources);

        /// <summary>
        /// The number of this page, 1-indexed.
        /// </summary>
        public int PageNumber { get; }

        /// <summary>
        /// The current size of the page.
        /// </summary>
        public PdfRectangle PageSize { get; set; }

        /// <summary>
        /// Access to the underlying data structures for advanced use cases.
        /// </summary>
        public IContentStream CurrentStream => currentStream;

        /// <summary>
        /// Access to
        /// </summary>
        public IReadOnlyList<IContentStream> ContentStreams => contentStreams;

        internal PdfPageBuilder(int number, PdfDocumentBuilder documentBuilder)
        {
            this.documentBuilder = documentBuilder ?? throw new ArgumentNullException(nameof(documentBuilder));
            PageNumber = number;

            currentStream = new DefaultContentStream();
            contentStreams = new List<IPageContentStream>() { currentStream };
        }

        internal PdfPageBuilder(
            int number,
            PdfDocumentBuilder documentBuilder,
            IEnumerable<CopiedContentStream> copied,
            Dictionary<NameToken, IToken> pageDict,
            List<(DictionaryToken token, PdfAction action)> links)
        {
            this.documentBuilder = documentBuilder ?? throw new ArgumentNullException(nameof(documentBuilder));
            this.links = links;
            PageNumber = number;
            pageDictionary = pageDict;
            contentStreams = new List<IPageContentStream>(copied);

            var writeableContentStream = new DefaultContentStream();
            if (contentStreams.Count > 0)
            {
                var lastGlobalTransform = contentStreams.LastOrDefault(x => x.GlobalTransform.HasValue);

                if (lastGlobalTransform?.GlobalTransform != null)
                {
                    var inverse = lastGlobalTransform.GlobalTransform.Value.Inverse();
                    writeableContentStream.Add(
                        new ModifyCurrentTransformationMatrix(
                            [
                                inverse.A,
                                inverse.B,
                                inverse.C,
                                inverse.D,
                                inverse.E,
                                inverse.F
                            ]
                        ));
                }
            }

            currentStream = writeableContentStream;
            contentStreams.Add(currentStream);
        }

        /// <summary>
        /// Allow to append a new content stream before the current one and select it
        /// </summary>
        public void NewContentStreamBefore()
        {
            var index = Math.Max(contentStreams.IndexOf(currentStream) - 1, 0);

            currentStream = new DefaultContentStream();
            contentStreams.Insert(index, currentStream);
        }

        /// <summary>
        /// Allow to append a new content stream after the current one and select it
        /// </summary>
        public void NewContentStreamAfter()
        {
            var index = Math.Min(contentStreams.IndexOf(currentStream) + 1, contentStreams.Count);

            currentStream = new DefaultContentStream();
            contentStreams.Insert(index, currentStream);
        }

        /// <summary>
        /// Select a content stream from the list, by his index
        /// </summary>
        /// <param name="index">index of the content stream to be selected</param>
        public void SelectContentStream(int index)
        {
            if (index < 0 || index >= contentStreams.Count)
            {
                throw new IndexOutOfRangeException(nameof(index));
            }

            currentStream = contentStreams[index];
        }

        /// <summary>
        /// Draws a line on the current page between two points with the specified line width.
        /// </summary>
        /// <param name="from">The first point on the line.</param>
        /// <param name="to">The last point on the line.</param>
        /// <param name="lineWidth">The width of the line in user space units.</param>
        public PdfPageBuilder DrawLine(PdfPoint from, PdfPoint to, double lineWidth = 1)
        {
            if (lineWidth != 1)
            {
                currentStream.Add(new SetLineWidth(lineWidth));
            }

            currentStream.Add(new BeginNewSubpath(from.X, from.Y));
            currentStream.Add(new AppendStraightLineSegment(to.X, to.Y));
            currentStream.Add(StrokePath.Value);

            if (lineWidth != 1)
            {
                currentStream.Add(new SetLineWidth(1));
            }

            return this;
        }

        /// <summary>
        /// Draws a rectangle on the current page starting at the specified point with the given width, height and line width.
        /// </summary>
        /// <param name="position">The position of the rectangle, for positive width and height this is the bottom-left corner.</param>
        /// <param name="width">The width of the rectangle.</param>
        /// <param name="height">The height of the rectangle.</param>
        /// <param name="lineWidth">The width of the line border of the rectangle.</param>
        /// <param name="fill">Whether to fill with the color set by <see cref="SetTextAndFillColor"/>.</param>
        public PdfPageBuilder DrawRectangle(PdfPoint position, double width, double height, double lineWidth = 1, bool fill = false)
        {
            if (lineWidth != 1)
            {
                currentStream.Add(new SetLineWidth(lineWidth));
            }

            currentStream.Add(new AppendRectangle(position.X, position.Y, width, height));

            if (fill)
            {
                currentStream.Add(FillPathEvenOddRuleAndStroke.Value);
            }
            else
            {
                currentStream.Add(StrokePath.Value);
            }

            if (lineWidth != 1)
            {
                currentStream.Add(new SetLineWidth(lineWidth));
            }

            return this;
        }

        /// <summary>
        /// Set the number of degrees by which the page is rotated clockwise when displayed or printed.
        /// </summary>
        public PdfPageBuilder SetRotation(PageRotationDegrees degrees)
        {
            rotation = degrees.Value;
            return this;
        }

        /// <summary>
        /// Draws a triangle on the current page with the specified points and line width.
        /// </summary>
        /// <param name="point1">Position of the first corner of the triangle.</param>
        /// <param name="point2">Position of the second corner of the triangle.</param>
        /// <param name="point3">Position of the third corner of the triangle.</param>
        /// <param name="lineWidth">The width of the line border of the triangle.</param>
        /// <param name="fill">Whether to fill with the color set by <see cref="SetTextAndFillColor"/>.</param>
        public PdfPageBuilder DrawTriangle(PdfPoint point1, PdfPoint point2, PdfPoint point3, double lineWidth = 1, bool fill = false)
        {
            if (lineWidth != 1)
            {
                currentStream.Add(new SetLineWidth(lineWidth));
            }

            currentStream.Add(new BeginNewSubpath(point1.X, point1.Y));
            currentStream.Add(new AppendStraightLineSegment(point2.X, point2.Y));
            currentStream.Add(new AppendStraightLineSegment(point3.X, point3.Y));
            currentStream.Add(new AppendStraightLineSegment(point1.X, point1.Y));

            if (fill)
            {
                currentStream.Add(FillPathEvenOddRuleAndStroke.Value);
            }
            else
            {
                currentStream.Add(StrokePath.Value);
            }

            if (lineWidth != 1)
            {
                currentStream.Add(new SetLineWidth(lineWidth));
            }

            return this;
        }

        /// <summary>
        /// Draws a circle on the current page centering at the specified point with the given diameter and line width.
        /// </summary>
        /// <param name="center">The center position of the circle.</param>
        /// <param name="diameter">The diameter of the circle.</param>
        /// <param name="lineWidth">The width of the line border of the circle.</param>
        /// <param name="fill">Whether to fill with the color set by <see cref="SetTextAndFillColor"/>.</param>
        public PdfPageBuilder DrawCircle(PdfPoint center, double diameter, double lineWidth = 1, bool fill = false)
        {
            DrawEllipsis(center, diameter, diameter, lineWidth, fill);

            return this;
        }

        /// <summary>
        /// Draws an ellipsis on the current page centering at the specified point with the given width, height and line width.
        /// </summary>
        /// <param name="center">The center position of the ellipsis.</param>
        /// <param name="width">The width of the ellipsis.</param>
        /// <param name="height">The height of the ellipsis.</param>
        /// <param name="lineWidth">The width of the line border of the ellipsis.</param>
        /// <param name="fill">Whether to fill with the color set by <see cref="SetTextAndFillColor"/>.</param>
        public PdfPageBuilder DrawEllipsis(PdfPoint center, double width, double height, double lineWidth = 1, bool fill = false)
        {
            width /= 2;
            height /= 2;

            // See here: https://spencermortensen.com/articles/bezier-circle/
            const double cc = 0.55228474983079;

            if (lineWidth != 1)
            {
                currentStream.Add(new SetLineWidth(lineWidth));
            }

            currentStream.Add(new BeginNewSubpath(center.X - width, center.Y));
            currentStream.Add(new AppendDualControlPointBezierCurve(
                center.X - width, center.Y + height * cc,
                center.X - width * cc, center.Y + height,
                center.X, center.Y + height
            ));
            currentStream.Add(new AppendDualControlPointBezierCurve(
                center.X + width * cc, center.Y + height,
                center.X + width, center.Y + height * cc,
                center.X + width, center.Y
            ));
            currentStream.Add(new AppendDualControlPointBezierCurve(
                center.X + width, center.Y - height * cc,
                center.X + width * cc, center.Y - height,
                center.X, center.Y - height
            ));
            currentStream.Add(new AppendDualControlPointBezierCurve(
                center.X - width * cc, center.Y - height,
                center.X - width, center.Y - height * cc,
                center.X - width, center.Y
            ));

            if (fill)
            {
                currentStream.Add(FillPathEvenOddRuleAndStroke.Value);
            }
            else
            {
                currentStream.Add(StrokePath.Value);
            }

            if (lineWidth != 1)
            {
                currentStream.Add(new SetLineWidth(lineWidth));
            }

            return this;
        }

        /// <summary>
        /// Sets the stroke color for any following operations to the RGB value. Use <see cref="ResetColor"/> to reset.
        /// </summary>
        /// <param name="r">Red - 0 to 255</param>
        /// <param name="g">Green - 0 to 255</param>
        /// <param name="b">Blue - 0 to 255</param>
        public PdfPageBuilder SetStrokeColor(byte r, byte g, byte b)
        {
            currentStream.Add(Push.Value);
            currentStream.Add(new SetStrokeColorDeviceRgb(RgbToDouble(r), RgbToDouble(g), RgbToDouble(b)));

            return this;
        }

        /// <summary>
        /// Sets the stroke color with the exact value between 0 and 1 for any following operations to the RGB value. Use <see cref="ResetColor"/> to reset.
        /// </summary>
        /// <param name="r">Red - 0 to 1</param>
        /// <param name="g">Green - 0 to 1</param>
        /// <param name="b">Blue - 0 to 1</param>
        internal PdfPageBuilder SetStrokeColorExact(double r, double g, double b)
        {
            currentStream.Add(Push.Value);
            currentStream.Add(new SetStrokeColorDeviceRgb(CheckRgbDouble(r, nameof(r)),
                CheckRgbDouble(g, nameof(g)), CheckRgbDouble(b, nameof(b))));

            return this;
        }

        /// <summary>
        /// Sets the fill and text color for any following operations to the RGB value. Use <see cref="ResetColor"/> to reset.
        /// </summary>
        /// <param name="r">Red - 0 to 255</param>
        /// <param name="g">Green - 0 to 255</param>
        /// <param name="b">Blue - 0 to 255</param>
        public PdfPageBuilder SetTextAndFillColor(byte r, byte g, byte b)
        {
            currentStream.Add(Push.Value);
            currentStream.Add(new SetNonStrokeColorDeviceRgb(RgbToDouble(r), RgbToDouble(g), RgbToDouble(b)));

            return this;
        }

        /// <summary>
        /// Restores the stroke, text and fill color to default (black).
        /// </summary>
        public PdfPageBuilder ResetColor()
        {
            currentStream.Add(Pop.Value);

            return this;
        }

        /// <summary>
        /// Calculates the size and position of each letter in a given string in the provided font without changing the state of the page. 
        /// </summary>
        /// <param name="text">The text to measure each letter of.</param>
        /// <param name="fontSize">The size of the font in user space units.</param>
        /// <param name="position">The position of the baseline (lower-left corner) to start drawing the text from.</param>
        /// <param name="font">
        /// A font added to the document using <see cref="PdfDocumentBuilder.AddTrueTypeFont"/>
        /// or <see cref="PdfDocumentBuilder.AddStandard14Font"/> methods.
        /// </param> 
        /// <returns>The letters from the input text with their corresponding size and position.</returns>
        public IReadOnlyList<Letter> MeasureText(string text, double fontSize, PdfPoint position, PdfDocumentBuilder.AddedFont font)
        {
            if (font is null)
            {
                throw new ArgumentNullException(nameof(font));
            }

            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            if (!documentBuilder.Fonts.TryGetValue(font.Id, out var fontStore))
            {
                throw new ArgumentException($"No font has been added to the PdfDocumentBuilder with Id: {font.Id}. " +
                                            $"Use {nameof(documentBuilder.AddTrueTypeFont)} to register a font.", nameof(font));
            }

            if (fontSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(fontSize), "Font size must be greater than 0");
            }

            var fontProgram = fontStore.FontProgram;

            var fm = fontProgram.GetFontMatrix();

            var textMatrix = TransformationMatrix.FromValues(1, 0, 0, 1, position.X, position.Y);

            var letters = DrawLetters(null, text, fontProgram, fm, fontSize, textMatrix);

            return letters;
        }

        /// <summary>
        /// Draws the text in the provided font at the specified position and returns the letters which will be drawn. 
        /// </summary>
        /// <param name="text">The text to draw to the page.</param>
        /// <param name="fontSize">The size of the font in user space units.</param>
        /// <param name="position">The position of the baseline (lower-left corner) to start drawing the text from.</param>
        /// <param name="font">
        /// A font added to the document using <see cref="PdfDocumentBuilder.AddTrueTypeFont"/>
        /// or <see cref="PdfDocumentBuilder.AddStandard14Font"/> methods.
        /// </param> 
        /// <returns>The letters from the input text with their corresponding size and position.</returns>
        public IReadOnlyList<Letter> AddText(string text, double fontSize, PdfPoint position, PdfDocumentBuilder.AddedFont font)
        {
            if (font is null)
            {
                throw new ArgumentNullException(nameof(font));
            }

            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            if (!documentBuilder.Fonts.TryGetValue(font.Id, out var fontStore))
            {
                throw new ArgumentException($"No font has been added to the PdfDocumentBuilder with Id: {font.Id}. " +
                                            $"Use {nameof(documentBuilder.AddTrueTypeFont)} to register a font.", nameof(font));
            }

            if (fontSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(fontSize), "Font size must be greater than 0");
            }

            var fontName = GetAddedFont(font);

            var fontProgram = fontStore.FontProgram;

            var fm = fontProgram.GetFontMatrix();

            var textMatrix = TransformationMatrix.FromValues(1, 0, 0, 1, position.X, position.Y);

            var letters = DrawLetters(fontName, text, fontProgram, fm, fontSize, textMatrix);

            currentStream.Add(BeginText.Value);
            currentStream.Add(new SetFontAndSize(fontName, fontSize));
            currentStream.Add(new MoveToNextLineWithOffset(position.X, position.Y));
            var bytesPerShow = new List<byte>();
            foreach (var letter in text)
            {
                if (char.IsWhiteSpace(letter))
                {
                    currentStream.Add(new ShowText(bytesPerShow.ToArray()));
                    bytesPerShow.Clear();
                }

                var b = fontProgram.GetValueForCharacter(letter);
                bytesPerShow.Add(b);
            }

            if (bytesPerShow.Count > 0)
            {
                currentStream.Add(new ShowText(bytesPerShow.ToArray()));
            }

            currentStream.Add(EndText.Value);

            return letters;
        }

        /// <summary>
        /// Set the text rendering mode. This will apply to all future calls to AddText until called again.
        ///
        /// To insert invisible text, for example output of OCR, use <c>TextRenderingMode.Neither</c>.
        /// </summary>
        /// <param name="mode">Text rendering mode to set.</param>
        public PdfPageBuilder SetTextRenderingMode(TextRenderingMode mode)
        {
            currentStream.Add(new SetTextRenderingMode(mode));

            return this;
        }

        private NameToken GetAddedFont(PdfDocumentBuilder.AddedFont font)
        {
            if (!documentFonts.TryGetValue(font.Id, out NameToken? value))
            {
                value = NameToken.Create($"F{nextFontId++}");
                var resources = pageDictionary.GetOrCreateDict(NameToken.Resources);
                var fonts = resources.GetOrCreateDict(NameToken.Font);
                while (fonts.ContainsKey(value))
                {
                    value = NameToken.Create($"F{nextFontId++}");
                }

                documentFonts[font.Id] = value;
                fonts[value] = font.Reference;
            }

            return value;
        }

        /// <summary>
        /// Adds the JPEG image represented by the input bytes at the specified location.
        /// </summary>
        public AddedImage AddJpeg(byte[] fileBytes, PdfRectangle placementRectangle)
        {
            using (var stream = new MemoryStream(fileBytes))
            {
                return AddJpeg(stream, placementRectangle);
            }
        }

        /// <summary>
        /// Adds the JPEG image represented by the input stream at the specified location.
        /// </summary>
        public AddedImage AddJpeg(Stream fileStream, PdfRectangle placementRectangle = default)
        {
            var startFrom = fileStream.Position;
            var info = JpegHandler.GetInformation(fileStream);

            if (placementRectangle.Equals(default(PdfRectangle)))
            {
                placementRectangle = new PdfRectangle(
                    0,
                    0,
                    info.Width,
                    info.Height);
            }

            byte[] data;
            using (var memory = new MemoryStream())
            {
                fileStream.Seek(startFrom, SeekOrigin.Begin);
                fileStream.CopyTo(memory);
                data = memory.ToArray();
            }

            NameToken colorSpace;
            if (info.NumberOfComponents == 1)
            {
                colorSpace = NameToken.Devicegray;
            }
            else if (info.NumberOfComponents == 4)
            {
                colorSpace = NameToken.Devicecmyk;
            }
            else
            {
                colorSpace = NameToken.Devicergb;
            }

            var imgDictionary = new Dictionary<NameToken, IToken>
            {
                {NameToken.Type, NameToken.Xobject },
                {NameToken.Subtype, NameToken.Image },
                {NameToken.Width, new NumericToken(info.Width) },
                {NameToken.Height, new NumericToken(info.Height) },
                {NameToken.BitsPerComponent, new NumericToken(info.BitsPerComponent)},
                {NameToken.ColorSpace, colorSpace},
                {NameToken.Filter, NameToken.DctDecode},
                {NameToken.Length, new NumericToken(data.Length)}
            };

            var reference = documentBuilder.AddImage(new DictionaryToken(imgDictionary), data);
            var resources = pageDictionary.GetOrCreateDict(NameToken.Resources);
            var xObjects = resources.GetOrCreateDict(NameToken.Xobject);

            var key = NameToken.Create(xobjectsNames.NewName());
            xObjects[key] = reference;

            currentStream.Add(Push.Value);
            // This needs to be the placement rectangle.
            currentStream.Add(new ModifyCurrentTransformationMatrix(new[]
            {
                placementRectangle.Width, 0,
                0, placementRectangle.Height,
                placementRectangle.BottomLeft.X, placementRectangle.BottomLeft.Y
            }));
            currentStream.Add(new InvokeNamedXObject(key));
            currentStream.Add(Pop.Value);

            return new AddedImage(reference.Data, info.Width, info.Height);
        }

        /// <summary>
        /// Adds the JPEG image previously added using <see cref="AddJpeg(byte[],PdfRectangle)"/>,
        /// this will share the same image data to prevent duplication.
        /// </summary>
        /// <param name="image">An image previously added to this page or another page.</param>
        /// <param name="placementRectangle">The size and location to draw the image on this page.</param>
        public void AddJpeg(AddedImage image, PdfRectangle placementRectangle) => AddImage(image, placementRectangle);

        /// <summary>
        /// Adds the image previously added using <see cref="AddJpeg(byte[], PdfRectangle)"/>
        /// or <see cref="AddPng(byte[], PdfRectangle)"/> sharing the same image to prevent duplication.
        /// </summary>
        public void AddImage(AddedImage image, PdfRectangle placementRectangle)
        {
            var resources = pageDictionary.GetOrCreateDict(NameToken.Resources);
            var xObjects = resources.GetOrCreateDict(NameToken.Xobject);

            var key = NameToken.Create(xobjectsNames.NewName());
            xObjects[key] = new IndirectReferenceToken(image.Reference);

            currentStream.Add(Push.Value);
            // This needs to be the placement rectangle.
            currentStream.Add(new ModifyCurrentTransformationMatrix(new[]
            {
                placementRectangle.Width, 0,
                0, placementRectangle.Height,
                placementRectangle.BottomLeft.X, placementRectangle.BottomLeft.Y
            }));
            currentStream.Add(new InvokeNamedXObject(key));
            currentStream.Add(Pop.Value);
        }

        /// <summary>
        /// Adds the PNG image represented by the input bytes at the specified location.
        /// </summary>
        public AddedImage AddPng(byte[] pngBytes, PdfRectangle placementRectangle)
        {
            using (var memoryStream = new MemoryStream(pngBytes))
            {
                return AddPng(memoryStream, placementRectangle);
            }
        }

        /// <summary>
        /// Adds the PNG image represented by the input stream at the specified location.
        /// </summary>
        public AddedImage AddPng(Stream pngStream, PdfRectangle placementRectangle = default)
        {
            var png = Png.Open(pngStream);

            if (placementRectangle.Equals(default(PdfRectangle)))
            {
                placementRectangle = new PdfRectangle(0, 0, png.Width, png.Height);
            }

            var data = new byte[png.Width * png.Height * 3];
            int pixelIndex = 0;
          
            for (var rowIndex = 0; rowIndex < png.Height; rowIndex++)
            {
                for (var colIndex = 0; colIndex < png.Width; colIndex++)
                {
                    var pixel = png.GetPixel(colIndex, rowIndex);

                    data[pixelIndex++] = pixel.R;
                    data[pixelIndex++] = pixel.G;
                    data[pixelIndex++] = pixel.B;
                }
            }

            var widthToken = new NumericToken(png.Width);
            var heightToken = new NumericToken(png.Height);

            IndirectReferenceToken? smaskReference = null;

            if (png.HasAlphaChannel && documentBuilder.ArchiveStandard != PdfAStandard.A1B && documentBuilder.ArchiveStandard != PdfAStandard.A1A)
            {
                var smaskData = new byte[data.Length / 3];
                for (var rowIndex = 0; rowIndex < png.Height; rowIndex++)
                {
                    for (var colIndex = 0; colIndex < png.Width; colIndex++)
                    {
                        var pixel = png.GetPixel(colIndex, rowIndex);

                        var index = rowIndex * png.Width + colIndex;
                        smaskData[index] = pixel.A;
                    }
                }

                var compressedSmask = DataCompresser.CompressBytes(smaskData);

                // Create a soft-mask.
                var smaskDictionary = new Dictionary<NameToken, IToken>
                {
                    {NameToken.Type, NameToken.Xobject},
                    {NameToken.Subtype, NameToken.Image},
                    {NameToken.Width, widthToken},
                    {NameToken.Height, heightToken},
                    {NameToken.ColorSpace, NameToken.Devicegray},
                    {NameToken.BitsPerComponent, new NumericToken(8)},
                    {NameToken.Decode, new ArrayToken(new IToken[] { new NumericToken(0), new NumericToken(1) })},
                    {NameToken.Length, new NumericToken(compressedSmask.Length)},
                    {NameToken.Filter, NameToken.FlateDecode}
                };

                smaskReference = documentBuilder.AddImage(new DictionaryToken(smaskDictionary), compressedSmask);
            }

            var compressed = DataCompresser.CompressBytes(data);

            var imgDictionary = new Dictionary<NameToken, IToken>
            {
                {NameToken.Type, NameToken.Xobject},
                {NameToken.Subtype, NameToken.Image},
                {NameToken.Width, widthToken},
                {NameToken.Height, heightToken},
                {NameToken.BitsPerComponent, new NumericToken(8)},
                {NameToken.ColorSpace, NameToken.Devicergb},
                {NameToken.Filter, NameToken.FlateDecode},
                {NameToken.Length, new NumericToken(compressed.Length)}
            };

            if (smaskReference != null)
            {
                imgDictionary.Add(NameToken.Smask, smaskReference);
            }

            var reference = documentBuilder.AddImage(new DictionaryToken(imgDictionary), compressed);

            var resources = pageDictionary.GetOrCreateDict(NameToken.Resources);
            var xObjects = resources.GetOrCreateDict(NameToken.Xobject);

            var key = NameToken.Create(xobjectsNames.NewName());

            xObjects[key] = reference;

            currentStream.Add(Push.Value);
            // This needs to be the placement rectangle.
            currentStream.Add(new ModifyCurrentTransformationMatrix(
            [
                placementRectangle.Width, 0,
                0, placementRectangle.Height,
                placementRectangle.BottomLeft.X, placementRectangle.BottomLeft.Y
            ]));
            currentStream.Add(new InvokeNamedXObject(key));
            currentStream.Add(Pop.Value);

            return new AddedImage(reference.Data, png.Width, png.Height);
        }

        /// <summary>
        /// Copy a page from unknown source to this page
        /// </summary>
        /// <param name="srcPage">Page to be copied</param>
        public PdfPageBuilder CopyFrom(Page srcPage)
        {
            if (currentStream.Operations.Count > 0)
            {
                NewContentStreamAfter();
            }

            var destinationStream = currentStream;

            if (!srcPage.Dictionary.TryGet(NameToken.Resources, srcPage.pdfScanner, out DictionaryToken? srcResourceDictionary))
            {
                // If the page doesn't have resources, then we copy the entire content stream, since not operation would collide 
                // with the ones already written
                destinationStream.Operations.AddRange(srcPage.Operations);
                return this;
            }

            // TODO: How should we handle any other token in the page dictionary (Eg. LastModified, MediaBox, CropBox, BleedBox, TrimBox, ArtBox,
            //      BoxColorInfo, Rotate, Group, Thumb, B, Dur, Trans, Annots, AA, Metadata, PieceInfo, StructParents, ID, PZ, SeparationInfo, Tabs,
            //      TemplateInstantiated, PresSteps, UserUnit, VP)

            var operations = new List<IGraphicsStateOperation>(srcPage.Operations);

            // We need to relocate the resources, and we have to make sure that none of the resources collide with 
            // the already written operation's resources

            var resources = pageDictionary.GetOrCreateDict(NameToken.Resources, srcPage.pdfScanner);

            foreach (var set in srcResourceDictionary.Data)
            {
                var nameToken = NameToken.Create(set.Key);
                if (nameToken == NameToken.Font || nameToken == NameToken.Xobject)
                {
                    // We have to skip this two because we have a separate dictionary for them
                    continue;
                }

                if (!resources.ContainsKey(nameToken))
                {
                    // It means that this type of resources doesn't currently exist in the page, so we can copy it
                    // with no problem
                    resources[nameToken] = documentBuilder.CopyToken(srcPage.pdfScanner, set.Value);
                    continue;
                }

                // TODO: I need to find a test case
                // It would have ExtendedGraphics or colorspaces, etc...
            }

            // Special cases
            // Since we don't directly add font's to the pages resources, we have to go look at the document's font
            if (srcResourceDictionary.TryGet(NameToken.Font, srcPage.pdfScanner, out DictionaryToken? fontsDictionary))
            {
                var pageFontsDictionary = resources.GetOrCreateDict(NameToken.Font, srcPage.pdfScanner);

                foreach (var fontSet in fontsDictionary.Data)
                {
                    var fontName = NameToken.Create(fontSet.Key);
                    if (pageFontsDictionary.ContainsKey(fontName))
                    {
                        // This would mean that the imported font collide with one of the added font. so we have to rename it
                        var newName = NameToken.Create($"F{nextFontId++}");
                        while (pageFontsDictionary.ContainsKey(newName))
                        {
                            newName = NameToken.Create($"F{nextFontId++}");
                        }

                        // Set all the pertinent SetFontAndSize operations with the new name
                        operations = operations.Select(op =>
                        {
                            if (!(op is SetFontAndSize fontAndSizeOperation))
                            {
                                return op;
                            }

                            if (fontAndSizeOperation.Font.Data == fontName)
                            {
                                return new SetFontAndSize(newName, fontAndSizeOperation.Size);
                            }

                            return op;
                        }).ToList();

                        fontName = newName;
                    }

                    if (!(fontSet.Value is IndirectReferenceToken fontReferenceToken))
                    {
                        throw new PdfDocumentFormatException($"Expected a IndirectReferenceToken for the font, got a {fontSet.Value.GetType().Name}");
                    }

                    pageFontsDictionary.Add(fontName, documentBuilder.CopyToken(srcPage.pdfScanner, fontReferenceToken));
                }
            }

            // Since we don't directly add xobjects's to the pages resources, we have to go look at the document's xobjects
            if (srcResourceDictionary.TryGet(NameToken.Xobject, srcPage.pdfScanner, out DictionaryToken? xobjectsDictionary))
            {
                var pageXobjectsDictionary = resources.GetOrCreateDict(NameToken.Xobject, srcPage.pdfScanner);

                foreach (var xobjectSet in xobjectsDictionary.Data)
                {
                    var xobjectName = xobjectSet.Key;

                    // This would mean that the imported xobject collide with one of the added image. so we have to rename it
                    var newName = xobjectsNames.FixName(xobjectName);

                    if (xobjectName != newName)
                    {
                        // Set all the pertinent SetFontAndSize operations with the new name
                        operations = operations.Select(op =>
                        {
                            if (!(op is InvokeNamedXObject invokeNamedOperation))
                            {
                                return op;
                            }

                            if (invokeNamedOperation.Name.Data == xobjectName)
                            {
                                return new InvokeNamedXObject(NameToken.Create(newName));
                            }

                            return op;
                        }).ToList();
                    }

                    xobjectName = newName;

                    if (!(xobjectSet.Value is IndirectReferenceToken fontReferenceToken))
                    {
                        throw new PdfDocumentFormatException($"Expected a IndirectReferenceToken for the XObject, got a {xobjectSet.Value.GetType().Name}");
                    }

                    pageXobjectsDictionary[xobjectName] = documentBuilder.CopyToken(srcPage.pdfScanner, fontReferenceToken);
                }
            }

            // Since we don't directly add xobjects's to the pages resources, we have to go look at the document's xobjects
            if (srcResourceDictionary.TryGet(NameToken.ExtGState, srcPage.pdfScanner, out DictionaryToken? gsDictionary))
            {
                var pageGstateDictionary = resources.GetOrCreateDict(NameToken.ExtGState, srcPage.pdfScanner);

                foreach (var gstate in gsDictionary.Data)
                {
                    var gstateName = gstate.Key;
                    var newName = gStateNames.FixName(gstateName);

                    if (newName != gstateName)
                    {
                        // This would mean that the imported xobject collide with one of the added image. so we have to rename it

                        // Set all the pertinent SetFontAndSize operations with the new name
                        operations = operations.Select(op =>
                        {
                            if (!(op is SetGraphicsStateParametersFromDictionary invokeNamedOperation))
                            {
                                return op;
                            }

                            if (invokeNamedOperation.Name.Data == gstateName)
                            {
                                return new SetGraphicsStateParametersFromDictionary(NameToken.Create(newName));
                            }

                            return op;
                        }).ToList();

                        gstateName = newName;
                    }
                    
                    // According to PDF spec 32000-1:2008, section 8.4.5, ExtGState can contain both direct values and indirect references
                    if (gstate.Value is IndirectReferenceToken fontReferenceToken)
                    {
                        pageGstateDictionary[gstateName] = documentBuilder.CopyToken(srcPage.pdfScanner, fontReferenceToken);
                    }
                    else
                    {
                        pageGstateDictionary[gstateName] = documentBuilder.CopyToken(srcPage.pdfScanner, gstate.Value);
                    }
                }
            }

            // Reset the graphics state to what we'd expect to be able to write our content in the correct locations.
            var globalTransform = PdfContentTransformationReader.GetGlobalTransform(operations);
            if (globalTransform.HasValue)
            {
                var inverse = globalTransform.Value.Inverse();
                operations.Add(new ModifyCurrentTransformationMatrix(
                    [
                        inverse.A,
                        inverse.B,
                        inverse.C,
                        inverse.D,
                        inverse.E,
                        inverse.F
                    ]));
            }

            destinationStream.Operations.AddRange(operations);

            return this;
        }

        private List<Letter> DrawLetters(NameToken? name, string text, IWritingFont font, in TransformationMatrix fontMatrix, double fontSize, TransformationMatrix textMatrix)
        {
            var horizontalScaling = 1;
            var rise = 0;
            var letters = new List<Letter>();

            var renderingMatrix =
                TransformationMatrix.FromValues(fontSize * horizontalScaling, 0, 0, fontSize, 0, rise);

            var width = 0.0;

            textSequence++;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (!font.TryGetBoundingBox(c, out var rect))
                {
                    throw new InvalidOperationException($"The font does not contain a character: '{c}' (0x{(int)c:X}).");
                }

                if (!font.TryGetAdvanceWidth(c, out var charWidth))
                {
                    throw new InvalidOperationException($"The font does not contain a character: {c}.");
                }

                var advanceRect = new PdfRectangle(0, 0, charWidth, 0);
                advanceRect = textMatrix.Transform(renderingMatrix.Transform(fontMatrix.Transform(advanceRect)));

                var documentSpace = textMatrix.Transform(renderingMatrix.Transform(fontMatrix.Transform(rect)));

                var letter = new Letter(
                    c.ToString(),
                    documentSpace,
                    advanceRect.BottomLeft,
                    advanceRect.BottomRight,
                    width,
                    fontSize,
                    FontDetails.GetDefault(name),
                    TextRenderingMode.Fill,
                    GrayColor.Black,
                    GrayColor.Black,
                    fontSize,
                    textSequence);

                letters.Add(letter);

                var tx = advanceRect.Width * horizontalScaling;
                var ty = 0;

                var translate = TransformationMatrix.GetTranslationMatrix(tx, ty);

                width += tx;

                textMatrix = translate.Multiply(textMatrix);
            }

            return letters;
        }

        private static double RgbToDouble(byte value)
        {
            var res = Math.Max(0, value / (double)byte.MaxValue);
            res = Math.Round(Math.Min(1, res), 4);

            return res;
        }

        private static double CheckRgbDouble(double value, string argument)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(argument, $"Provided double for RGB color was less than zero: {value}.");
            }

            if (value > 1)
            {
                throw new ArgumentOutOfRangeException(argument, $"Provided double for RGB color was greater than one: {value}.");
            }

            return value;
        }

        /// <summary>
        /// Provides access to the raw page data structures for advanced editing use cases.
        /// </summary>
        public interface IContentStream
        {
            /// <summary>
            /// The operations making up the page content stream.
            /// </summary>
            List<IGraphicsStateOperation> Operations { get; }
        }

        internal interface IPageContentStream : IContentStream
        {
            bool ReadOnly { get; }

            bool HasContent { get; }

            void Add(IGraphicsStateOperation operation);

            IndirectReferenceToken Write(IPdfStreamWriter writer);

            /// <summary>
            /// If this content stream applied any global transform to the graphics state this will
            /// tell you which one is currently active at the end of this stream being applied.
            /// </summary>
            TransformationMatrix? GlobalTransform { get; }
        }

        internal class DefaultContentStream : IPageContentStream
        {
            private readonly List<IGraphicsStateOperation> operations;

            public DefaultContentStream() : this(new List<IGraphicsStateOperation>())
            {
            }

            public DefaultContentStream(List<IGraphicsStateOperation> operations)
            {
                this.operations = operations;
            }

            public bool ReadOnly => false;

            public bool HasContent => operations.Any();

            public TransformationMatrix? GlobalTransform => null;

            public void Add(IGraphicsStateOperation operation)
            {
                operations.Add(operation);
            }

            public List<IGraphicsStateOperation> Operations => operations;

            public IndirectReferenceToken Write(IPdfStreamWriter writer)
            {
                using (var memoryStream = new MemoryStream())
                {
                    foreach (var operation in operations)
                    {
                        operation.Write(memoryStream);
                    }

                    var bytes = memoryStream.ToArray();

                    var stream = DataCompresser.CompressToStream(bytes);

                    return writer.WriteToken(stream);
                }
            }
        }

        internal class CopiedContentStream : IPageContentStream
        {
            private readonly IndirectReferenceToken token;

            public bool ReadOnly => true;

            public bool HasContent => true;

            public TransformationMatrix? GlobalTransform { get; }

            public CopiedContentStream(
                IndirectReferenceToken indirectReferenceToken,
                TransformationMatrix? globalTransform)
            {
                GlobalTransform = globalTransform;
                token = indirectReferenceToken;
            }

            public IndirectReferenceToken Write(IPdfStreamWriter writer)
            {
                return token;
            }

            public void Add(IGraphicsStateOperation operation)
            {
                throw new NotSupportedException("Writing to a copied content stream is not supported.");
            }

            public List<IGraphicsStateOperation> Operations =>
                throw new NotSupportedException("Reading raw operations is not supported from a copied content stream.");
        }


        /// <summary>
        /// A key representing an image available to use for the current document builder.
        /// Create it by adding an image to a page using <see cref="AddJpeg(byte[],PdfRectangle)"/>.
        /// </summary>
        public class AddedImage
        {
            /// <summary>
            /// The Id uniquely identifying this image on the builder.
            /// </summary>
            internal Guid Id { get; }

            /// <summary>
            /// The reference to the stored image XObject.
            /// </summary>
            internal IndirectReference Reference { get; }

            /// <summary>
            /// The width of the raw image in pixels.
            /// </summary>
            public int Width { get; }

            /// <summary>
            /// The height of the raw image in pixels.
            /// </summary>
            public int Height { get; }

            /// <summary>
            /// Create a new <see cref="AddedImage"/>.
            /// </summary>
            internal AddedImage(IndirectReference reference, int width, int height)
            {
                Id = Guid.NewGuid();
                Reference = reference;
                Width = width;
                Height = height;
            }
        }
    }
}
