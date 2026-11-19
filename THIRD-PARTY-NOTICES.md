# Third-party notices

Reglet is licensed under the MIT License (see `LICENSE`). It contains data derived from the following third-party
sources, used under the terms reproduced here.

## Unicode Character Database (Reglet.Core)

`src/Reglet.Core/BidiData.cs` is generated from the Unicode Character Database (bidirectional classes, paired brackets and mirrored glyphs of Unicode 18.0.0; `DerivedBidiClass.txt`,
`BidiBrackets.txt`, `BidiMirroring.txt`) by `tools/generate-bidi-data.py`; the test suite uses an extract of
`BidiCharacterTest.txt` (Unicode 16.0.0).

```
UNICODE LICENSE V3

COPYRIGHT AND PERMISSION NOTICE

Copyright © 1991-2026 Unicode, Inc.

NOTICE TO USER: Carefully read the following legal agreement. BY
DOWNLOADING, INSTALLING, COPYING OR OTHERWISE USING DATA FILES, AND/OR
SOFTWARE, YOU UNEQUIVOCALLY ACCEPT, AND AGREE TO BE BOUND BY, ALL OF THE
TERMS AND CONDITIONS OF THIS AGREEMENT. IF YOU DO NOT AGREE, DO NOT
DOWNLOAD, INSTALL, COPY, DISTRIBUTE OR USE THE DATA FILES OR SOFTWARE.

Permission is hereby granted, free of charge, to any person obtaining a
copy of data files and any associated documentation (the "Data Files") or
software and any associated documentation (the "Software") to deal in the
Data Files or Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, and/or sell
copies of the Data Files or Software, and to permit persons to whom the
Data Files or Software are furnished to do so, provided that either (a)
this copyright and permission notice appear with all copies of the Data
Files or Software, or (b) this copyright and permission notice appear in
associated Documentation.

THE DATA FILES AND SOFTWARE ARE PROVIDED "AS IS", WITHOUT WARRANTY OF ANY
KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF
THIRD PARTY RIGHTS.

IN NO EVENT SHALL THE COPYRIGHT HOLDER OR HOLDERS INCLUDED IN THIS NOTICE
BE LIABLE FOR ANY CLAIM, OR ANY SPECIAL INDIRECT OR CONSEQUENTIAL DAMAGES,
OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS,
WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION,
ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THE DATA
FILES OR SOFTWARE.

Except as contained in this notice, the name of a copyright holder shall
not be used in advertising or otherwise to promote the sale, use or other
dealings in these Data Files or Software without prior written
authorization of the copyright holder.
```

## Adobe Font Metrics for the PDF core 14 fonts (Reglet.Pdf)

`src/Reglet.Pdf/PdfStandardFontMetrics.cs` contains the advance widths of the printable ASCII characters of the
Helvetica, Times and Courier families, taken from Adobe's AFM files for the PDF core 14 fonts (Copyright (c) 1985,
1987, 1989, 1990, 1997 Adobe Systems Incorporated. All Rights Reserved). The AFM files themselves are not distributed;
the widths were copied into a table and are used together with Reglet's own ascender values (a modification).

```
This file and the 14 PostScript(R) AFM files it accompanies may be
used, copied, and distributed for any purpose and without charge,
with or without modification, provided that all copyright notices are
retained; that the AFM files are not distributed without this file;
that all modifications to this file or any of the AFM files are
prominently noted in the modified file(s); and that this paragraph is
not modified. Adobe Systems has no responsibility or obligation to
support the use of the AFM files.
```

## Hyphenation patterns (Reglet.Hyphenation)

The optional package `Reglet.Hyphenation` contains TeX hyphenation patterns of the `hyph-utf8` project. Their
authors and licences are listed in `src/Reglet.Hyphenation/THIRD-PARTY-NOTICES.md`, which ships inside that package.
