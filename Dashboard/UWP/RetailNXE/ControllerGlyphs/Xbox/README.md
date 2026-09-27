gamepad-flat-icons
==================
This is a free set of flat icons that can be used in any of your game dev projects - commercial or non-commercial.

This repository contains the source file that the icon set is built from. If you want to download a ready to use set, then head over to the release page on Itch.io.

Sample
------
![](sample.png)


Naming Convention
-----------------
Each file starts with a prefix of `generic_`, `ps_`, `xb_` or `switch_` denoting generic icons, PlayStation icons, Xbox icons and Nintendo Switch icons respectively. They are then followed by the name of the button the icon represents and end with a DPI indicator of either `50`, `96`, `150` or `300`.

Resolutions
-----------
All the icons have a consistent width and height with the exception of the icons representing the shoulder buttons (L1, LB, L) and the generic start and select buttons. These icons, instead, have a width consistent with the rest of the icons, but heights that are unique to them; allowing for consistent horizontal spacing with the rest of the icon set.

The resolution of the icons for each DPI are as follows:

- 50dpi: `44x44` (`44x23` for the exceptions listed above)
- 96dpi:  `84x84` (`84x45` for the exceptions listed above)
- 150dpi: `132x132` (`132x70` for the exceptions listed above)
- 300dpi: `264x264` (`264x139` for the exceptions listed above)

What is Included?
-----------------
Below is the full list of included icons and their respective file names.

### Generic Icons
- D-pad Up Button: `generic_dpad_up`
- D-pad Right Button: `generic_dpad_right`
- D-pad Down Button: `generic_dpad_down`
- D-pad Left Button: `generic_dpad_left`
- Right Analogue Stick: `generic_right_stick`
- Left Analogue Stick: `generic_left_stick`
- L3 Button: `generic_l3`
- R3 Button: `generic_r3`
- Left Analogue Stick (Left): `generic_left_stick_left`
- Left Analogue Stick (Up): `generic_left_stick_up`
- Left Analogue Stick (Right): `generic_left_stick_right`
- Left Analogue Stick (Down): `generic_left_stick_down`
- Right Analogue Stick (Left): `generic_right_stick_left`
- Right Analogue Stick (Up): `generic_right_stick_up`
- Right Analogue Stick (Right): `generic_right_stick_right`
- Right Analogue Stick (Down): `generic_right_stick_down`
- Select Button: `generic_select`
- Start Button: `generic_start`

### PlayStation Icons
- Triangle Button: `ps_triangle`
- Square Button: `ps_square`
- X Button: `ps_x`
- Circle Button: `ps_circle`
- PlayStation Super Button: `ps_super`
- L1 Shoulder Button: `ps_l1`
- R1 Shoulder Button: `ps_r1`
- L2 Trigger: `ps_l2`
- R2 Trigger: `ps_r2`

### Xbox Icons
- Y Button: `xb_y`
- X Button: `xb_x`
- B Button: `xb_b`
- A Button: `xb_a`
- Right Trigger: `xb_rt`
- Right Shoulder Button: `xb_rb`
- Left Shoulder Button: `xb_lb`
- Left Trigger: `xb_lt`
- Select Button: `xb_select`
- Xbox Super Button: `xb_super`
- Start Button: `xb_start`

### Nintendo Switch Icons
- Left Trigger: `switch_zl`
- Right Trigger: `switch_zr`
- Right Shoulder Button: `switch_r`
- Left Shoulder Button: `switch_l`
- Minus Button: `switch_minus`
- Plus Button: `switch_plus`
- Y Button: `switch_y`
- X Button: `switch_x`
- B Button: `switch_b`
- A Button: `switch_a`
- Home / Super Button: `switch_super`


Building from Source
--------------------
To build the icons from the source SVG file, [Inkscape](https://inkscape.org/) must be installed and available in your global path. When setup, open a terminal in the source directory and run the `build.sh` script. An icon set exported at 50, 96, 150 and 300dpi will be exported into the `build` directory.


Contributing
------------
Know of a popular game pad that you think should be included in this set? Let me know and I'll try to get it added. Alternatively, check [CONTRIBUTING.md](CONTRIBUTING.md) for information on adding it yourself and submitting it to the project.


License
-------
This project is licensed under the Creative Commons Attribution-ShareAlike 4.0 International license. A copy of the license can be found in [LICENSE.md](LICENSE.md) and online at https://creativecommons.org/licenses/by-sa/4.0/
