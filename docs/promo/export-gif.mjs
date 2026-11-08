// Convert the verified HyperFrames MP4 to a compact, endlessly looping README GIF.
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, rmdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const project = dirname(fileURLToPath(import.meta.url));
const output = join(project, '..', 'media');
const input = join(output, 'serial-monitor-promo-ko.mp4');
const ffmpeg = process.env.FFMPEG_PATH || 'ffmpeg';
const scratch = mkdtempSync(join(tmpdir(), 'serial-monitor-promo-'));
const palette = join(scratch, 'palette.png');
const filter = 'fps=12,scale=960:540:flags=lanczos';
const run = (args) => {
  const result = spawnSync(ffmpeg, ['-hide_banner', '-loglevel', 'error', '-y', ...args], { stdio: 'inherit', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`FFmpeg failed with exit code ${result.status}`);
};

mkdirSync(output, { recursive: true });
try {
  run(['-i', input, '-vf', `${filter},palettegen=max_colors=192:stats_mode=diff`, '-frames:v', '1', palette]);
  run(['-i', input, '-i', palette, '-filter_complex', `[0:v]${filter}[scaled];[scaled][1:v]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle`, '-an', '-loop', '0', join(output, 'serial-monitor-promo-ko.gif')]);
  console.log('Created docs/media/serial-monitor-promo-ko.gif (960×540, 12 fps, loop forever)');
} finally {
  rmSync(palette, { force: true });
  rmdirSync(scratch);
}
