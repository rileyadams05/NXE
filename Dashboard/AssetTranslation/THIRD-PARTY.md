# Third-party tooling

`XUIHelper` is used as a build-time converter for the Xbox 360 XUI/XUR formats.

- Source: https://github.com/SGCSam/XUIHelper
- Pinned revision recorded by each translation report
- License: GPL-3.0 (see the dependency repository)
- Role: converts the supplied retail NXE 9199 XUR v5 scenes to XUI v12

The converter is not part of the modern-Xbox runtime package. Generated dashboard data and the standalone renderer remain separate from the converter executable.

Local compatibility fixes are intentionally narrow:

- register the `LiveVisionControl` class present in retail build 9199;
- remove XML-forbidden control characters when serializing legacy string properties;
- require every successful CLI invocation to actually produce its requested output file.
