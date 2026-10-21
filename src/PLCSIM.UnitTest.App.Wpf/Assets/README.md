# Application Artwork

`PLCSIM.UnitTest.svg` is the canonical source for the PLCSIM.UnitTest product
mark. The artwork was created specifically for this project and is owned by
Lorenz Software. It is an original composition of the `plcSIM` wordmark, test
result symbols, and a signal trace. It does not incorporate Siemens product
icons, Siemens visual identity, or third-party icon artwork.

The checked-in derivatives serve environments that do not consume the source
SVG directly:

- `PLCSIM.UnitTest.png` is the 512 x 512 application and help image.
- `PLCSIM.UnitTest.ico` is the multi-resolution Windows application icon.
- `PLCSIM.UnitTest.WizardSmall.svg` and its PNG export provide the framed
  installer header mark.
- `installer/windows/assets/PLCSIM.UnitTest.Wizard.svg` composes the product
  mark with the Lorenz Software publisher identity for the installer sidebar.
  Its PNG export is consumed by Inno Setup.

The SVG files are the editable sources. PNG and ICO files are raster exports
and must be regenerated after changing a source SVG. Preserve transparency and
inspect both the full-size artwork and the Windows 16, 24, 32, 48, and 256 pixel
icon representations before accepting an update. The installer exports must
also be inspected on the Inno Setup welcome, component-selection, and summary
pages.

`LogoLorenzSoftware.png` is separate publisher artwork owned by Lorenz Software;
it is not part of the PLCSIM.UnitTest product mark.
