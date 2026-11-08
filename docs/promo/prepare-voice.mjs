// Rebuild the 20-second voice track from the five supplied MP3s. No network calls.
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, rmdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const project = dirname(fileURLToPath(import.meta.url));
const config = JSON.parse(readFileSync(join(project, 'voiceover.json'), 'utf8'));
const ffmpeg = process.env.FFMPEG_PATH || 'ffmpeg';
const ffprobe = process.env.FFPROBE_PATH || 'ffprobe';
const temporaryFiles = [];
const parts = [];
const samplePerFrame = config.sampleRate / config.fps;
if (!Number.isInteger(samplePerFrame) || config.segments.reduce((sum, s) => sum + s.frames, 0) !== 600) {
  throw new Error('Voice slots must total 600 video frames with whole audio samples.');
}
const scratch = mkdtempSync(join(tmpdir(), 'serial-monitor-voice-'));

function run(binary, args) {
  const result = spawnSync(binary, args, { encoding: 'utf8', maxBuffer: 16 * 1024 * 1024, windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${binary} failed: ${result.stderr}`);
  return result;
}
function duration(path) {
  return Number(JSON.parse(run(ffprobe, ['-v', 'error', '-show_entries', 'format=duration', '-of', 'json', path]).stdout).format.duration);
}
function temporary(name) {
  const path = join(scratch, name);
  temporaryFiles.push(path);
  return path;
}

try {
  let frame = 0;
  for (const segment of config.segments) {
    const source = join(project, segment.source);
    const hash = createHash('sha256').update(readFileSync(source)).digest('hex');
    if (hash !== segment.sha256) throw new Error(`${segment.id}: source changed; review trimming and timing first.`);
    const base = `atrim=end=${segment.trimEnd},asetpts=PTS-STARTPTS,atempo=${config.speed}`;
    const settings = `I=${config.targetLufs}:TP=${config.truePeakDb}:LRA=7`;
    const analysis = run(ffmpeg, ['-hide_banner', '-nostats', '-i', source, '-af', `${base},loudnorm=${settings}:print_format=json`, '-f', 'null', '-']);
    const jsonStart = analysis.stderr.lastIndexOf('{');
    const stats = JSON.parse(analysis.stderr.slice(jsonStart, analysis.stderr.indexOf('}', jsonStart) + 1));
    const values = ['input_i', 'input_tp', 'input_lra', 'input_thresh', 'target_offset'];
    if (!values.every(key => Number.isFinite(Number(stats[key])))) throw new Error(`${segment.id}: unreadable or silent source.`);
    const normalization = `loudnorm=${settings}:measured_I=${stats.input_i}:measured_TP=${stats.input_tp}:measured_LRA=${stats.input_lra}:measured_thresh=${stats.input_thresh}:offset=${stats.target_offset}:linear=true`;
    const normalized = temporary(`${segment.id}-normalized.wav`);
    run(ffmpeg, ['-v', 'error', '-y', '-i', source, '-af', `${base},${normalization}`, '-ar', String(config.sampleRate), '-ac', '1', '-c:a', 'pcm_s16le', normalized]);
    const spokenSeconds = duration(normalized);
    const slotSeconds = segment.frames / config.fps;
    if (spokenSeconds + segment.headMs / 1000 > slotSeconds - .015) throw new Error(`${segment.id}: voice exceeds its slot; refusing to cut speech.`);
    const samples = segment.frames * samplePerFrame;
    const filter = `afade=t=in:d=0.006,afade=t=out:st=${Math.max(0, spokenSeconds - .006)}:d=0.006,adelay=${segment.headMs}:all=1,apad=whole_len=${samples},atrim=end_sample=${samples}`;
    const part = temporary(`${segment.id}-slot.wav`);
    run(ffmpeg, ['-v', 'error', '-y', '-i', normalized, '-af', filter, '-ar', String(config.sampleRate), '-ac', '1', '-c:a', 'pcm_s16le', part]);
    parts.push(part);
    console.log(`${segment.id}: ${(frame / config.fps).toFixed(3)}–${((frame + segment.frames) / config.fps).toFixed(3)}s; voice ${spokenSeconds.toFixed(3)}s; trailing room ${(slotSeconds - spokenSeconds - segment.headMs / 1000).toFixed(3)}s`);
    frame += segment.frames;
  }
  const output = join(project, 'assets', 'voice', 'narration.wav');
  mkdirSync(dirname(output), { recursive: true });
  const labels = parts.map((_, i) => `[${i}:a]`).join('');
  run(ffmpeg, ['-v', 'error', '-y', ...parts.flatMap(p => ['-i', p]), '-filter_complex', `${labels}concat=n=${parts.length}:v=0:a=1[voice]`, '-map', '[voice]', '-ar', String(config.sampleRate), '-ac', '1', '-c:a', 'pcm_s16le', output]);
  if (Math.abs(duration(output) - 20) > 1 / config.sampleRate) throw new Error('Combined voice track must be exactly 20 seconds.');
  console.log('Created assets/voice/narration.wav: 20.000s, pitch preserved, two-pass loudness normalization.');
} finally {
  for (const path of temporaryFiles) rmSync(path, { force: true });
  rmdirSync(scratch);
}
