# Muzzle flash on the lens: simulation

Standalone test for the on-screen muzzle flash suggested for the Ravenfield VR mod (task 209 in its Tasks2.md). It is
not part of the mod's code.

A right-handed player fires eight guns at 90 frames a second: pistol, pistol close to the face, rifle (semi and full
auto), SMG, shotgun, suppressed rifle and machine gun. Every shot puts the approved see-through flash on both lenses:
- a hot core with a ragged orange bloom;
- a short starburst;
- lens ghosts and a halo;
- a soft wash from the gun's side.

The muzzle moves the whole time, so every flash lands somewhere new and the lens ghosts move with it:
- the player looks around while aiming: about 2.5 degrees with a stock at the shoulder, 5 for a pistol held out,
  6 for an SMG from the hip;
- breathing sway and a slight tremor;
- recoil kicks it up and a little sideways, and a burst climbs on full auto;
- each flash also varies: its centre sits a little ahead of the muzzle (the plume), it varies in size, and its
  starburst turns.

## Run

```
python flash_sim.py           # all guns: tests, GIFs, 90 fps videos and a chart in ./out
python flash_sim.py --live    # a window: Space or the mouse fires (hold for automatic guns), 1-8 gun, L limiter
python flash_sim.py --all-guns # only the all-guns animation (high quality GIF and lossless WebP)
python flash_sim.py --hd 1.6  # all guns rendered 1.6x bigger: all_guns_hd.gif and all_guns_hd.mp4
```

`--hd` draws the effect itself at the bigger size, so it is sharp, not stretched. Shrunk back, it matches the tested
size to within 0.24% on average. The tests always run at the tested 300 x 250 eye view.

Needs Python 3 with numpy and Pillow. OpenCV (`cv2`) adds the MP4 videos.

## Outputs (`out/`)

- `<gun>.gif`: no limiter (top) and with it (bottom), 30 fps.
- `<gun>_90fps.mp4`: the limiter on, at the headset's real 90 fps.
- `all_guns.gif`: every gun at once (606 x 570), made for quality:
  - each eye is rendered in full and shrunk with a high-quality filter;
  - text and lens outlines are drawn crisp at the final size;
  - it runs at 45 fps (two headset frames averaged per picture);
  - one palette serves the whole animation, built from every brightness level of the flashes and dithered, so there
    is no banding, palette flicker or sparkling cores.
- `all_guns.webp`: the same animation lossless in full colour, the best quality. It opens in any browser.
- `all_guns_hd.gif` and `all_guns_hd.mp4` (with `--hd`): every gun at once, high resolution. The MP4 is at 90 fps.
- `timeline.png`: the right eye's brightest 10-degree field per frame, with and without the limiter.
- `report.txt`: the test results.

## Tests

- Each flash is short: at most 67 ms (6 frames).
- It never reaches full white: the effect is capped at 94%.
- The eye nearer the muzzle gets the stronger flash, checked shot by shot on the rendered picture.
- A suppressed rifle is faint: under 15% of the rifle's flash.
- The flash lands somewhere new each shot: it spreads at least 5 px and typically moves 2 px or more between shots
  (two shots of a burst, 80 ms apart, can land close together, as a real muzzle does).
- Photosensitivity: at most 3 flashes (6 transitions) in any second (WCAG 2.3.1 / broadcast flash rule), measured
  over a 10-degree visual field. In a headset that is a small patch of the view, so the check is stricter than on a
  TV. Without a limiter, full auto would flash up to 15 times a second.

## The flash limiter

The limiter keeps a budget of 6 transitions in any 1.1 s:
- a normal flash costs 2 (on, off);
- a very bright one costs 3, because it fades in two counted steps;
- when only 2 are left, a flash is capped at the strongest level that fades in one step;
- after that, shots get a soft flash below the threshold, and sustained fire gives a steady glow instead of a strobe.

The soft and one-step levels are calibrated for the glow under them. On a lit base, the same step in the picture is a
bigger change in light, because screen values are not linear in light.
