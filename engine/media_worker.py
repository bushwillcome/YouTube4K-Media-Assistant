"""Local media pipeline. Stdin is private configuration; stdout is safe structured events.

No raw yt-dlp diagnostics, metadata, signed URLs or authentication values are logged.
GPU libraries load only if the requested transcript needs speech recognition.
"""
import contextlib
import hashlib
import html
import json
import math
import os
from pathlib import Path
import queue
import re
import subprocess
import sys
import threading
import time
import uuid
from urllib.parse import parse_qs, urlsplit


AUTH_ERRORS = {'authentication', 'cookies_locked', 'decrypt'}


class MediaError(Exception):
    def __init__(self, code):
        self.code = code
        super().__init__(code)


def emit(kind, **data):
    # ASCII-safe events also work with Windows consoles using non-UTF-8 code pages.
    print('@@MEDIA ' + json.dumps({'kind': kind, **data}, ensure_ascii=True), flush=True)


def error_code(text):
    low = text.lower()
    if any(word in low for word in ('dpapi', 'decrypt', 'app-bound', 'keyring')):
        return 'decrypt'
    if 'cookie' in low and any(word in low for word in ('database', 'locked', 'copy', 'permission', 'could not', 'unable')):
        return 'cookies_locked'
    if ('bot' in low and 'confirm' in low) or 'sign in' in low or 'log in' in low:
        return 'authentication'
    if 'cookie' in low:
        return 'cookies_locked'
    if any(word in low for word in ('unavailable', 'private video', 'removed', 'not available')):
        return 'unavailable'
    if any(word in low for word in ('timed out', 'timeout', 'connection', 'resolve', 'network')):
        return 'network'
    return 'download'


def root_language(code):
    return (code or '').lower().split('-')[0]


def choose_subtitle(info, language='auto'):
    """Only select an original track. Never choose an arbitrary translated language."""
    manual, automatic = info.get('subtitles') or {}, info.get('automatic_captions') or {}
    original = info.get('language')
    if not original:
        original = next((key[:-5] for key in automatic if key.endswith('-orig')), None)
    if not original and language != 'auto':
        original = language  # Explicit user hint about the original spoken language.
    if not original:
        return None
    wanted = original if language == 'auto' else language
    if root_language(wanted) != root_language(original):
        return None
    for kind, tracks in (('manual', manual), ('automatic', automatic)):
        keys = sorted(tracks, key=lambda k: (k != wanted, not k.endswith('-orig'), k))
        for key in keys:
            if key == 'live_chat' or root_language(key) != root_language(wanted):
                continue
            formats = tracks[key] or []
            if any(f.get('ext') == 'json3' and 'tlang' not in parse_qs(urlsplit(f.get('url', '')).query)
                   for f in formats):
                return kind, key
    return None


def parse_json3(data):
    segments = []
    previous_raw, previous_end = '', -1.0
    for event in data.get('events', []):
        try:
            start = float(event['tStartMs']) / 1000
            duration = max(0, float(event.get('dDurationMs', 0))) / 1000
            if not math.isfinite(start + duration) or start < 0:
                continue
            text = ''.join(part.get('utf8', '') for part in event.get('segs', []))
            text = re.sub(r'\s+', ' ', html.unescape(text)).strip()
            if not text:
                continue
            raw = text
            # YouTube rolling captions can repeat the preceding window. Restrict
            # deduplication to overlapping windows so intentional repeats survive.
            if segments and start < previous_end:
                if text == previous_raw:
                    segments[-1]['end'] = max(segments[-1]['end'], start + duration)
                    previous_end = max(previous_end, start + duration)
                    continue
                for size in range(min(len(previous_raw), len(text)), 1, -1):
                    if previous_raw[-size:] == text[:size]:
                        text = text[size:].lstrip()
                        break
            previous_raw, previous_end = raw, start + duration
            if text:
                segments.append({'start': start, 'end': start + duration, 'text': text})
        except (KeyError, TypeError, ValueError):
            continue
    return segments


def file_stem(title, video_id, suffix):
    title = re.sub(r'[<>:"/\\|?*\x00-\x1f]', '_', title).strip(' .')[:100].rstrip(' .') or '视频'
    return '{} [{}]{}'.format(title, video_id, suffix)


def video_format(height):
    if height not in (4320, 3456, 2160, 1440, 1080, 720):
        raise MediaError('configuration')
    limits = f'[height<={height}]' + ('[width<=7680]' if height == 4320 else '[width<=6144]' if height == 3456 else '')
    return 'bestvideo' + limits + '+bestaudio/best' + limits


def timestamp(seconds):
    value = max(0, int(seconds))
    return f'{value // 3600:02d}:{value // 60 % 60:02d}:{value % 60:02d}'


def export_transcript(base, title, segments, source, language):
    segments = [s for s in segments if s.get('text', '').strip()]
    if not segments:
        raise ValueError('empty transcript')
    paths = {'txt': Path(str(base) + '.txt'), 'md': Path(str(base) + '.md')}
    if any(p.exists() for p in paths.values()):
        raise FileExistsError('transcript already exists')
    contents = {
        'txt': '\n'.join(s['text'].strip() for s in segments) + '\n',
        'md': '# ' + title.replace('\n', ' ') + '\n\n> 原话文字稿 · ' + source + ' · 语言：' + language +
              '\n> 自动提取/识别结果，请按需校对。\n\n' + ''.join(
                  '[{} – {}] {}\n\n'.format(timestamp(s['start']), timestamp(s['end']), s['text'].strip())
                  for s in segments),
    }
    temporary, committed = [], []
    try:
        for kind, path in paths.items():
            path.parent.mkdir(parents=True, exist_ok=True)
            temp = path.with_name(path.name + '.' + uuid.uuid4().hex + '.partial')
            temporary.append(temp)
            with temp.open('x', encoding='utf-8-sig', newline='\n') as stream:
                stream.write(contents[kind])
                stream.flush()
                os.fsync(stream.fileno())
        for temp, path in zip(temporary, paths.values()):
            # Windows rename fails when the target exists; never overwrite user text.
            temp.rename(path)
            committed.append(path)
        return {key: str(value) for key, value in paths.items()}
    except Exception:
        for path in committed:
            path.unlink(missing_ok=True)
        raise
    finally:
        for temp in temporary:
            temp.unlink(missing_ok=True)


def kill_owned(process):
    if process.poll() is None:
        subprocess.run(['taskkill.exe', '/PID', str(process.pid), '/T', '/F'],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                       creationflags=subprocess.CREATE_NO_WINDOW, timeout=10)
        process.wait(timeout=10)


def run_command(args, phase, idle_timeout=120, authentication=False):
    """Capture external output in memory only; propagate fixed categories/progress."""
    channel = queue.Queue()
    process = subprocess.Popen(args, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, text=True, encoding='utf-8', errors='replace',
                               creationflags=subprocess.CREATE_NO_WINDOW)
    def reader(stream, is_error):
        try:
            for line in stream:
                channel.put((is_error, line))
        finally:
            stream.close()
            channel.put((is_error, None))
    threads = [threading.Thread(target=reader, args=(stream, error), daemon=True)
               for stream, error in ((process.stdout, False), (process.stderr, True))]
    for thread in threads:
        thread.start()
    output, diagnostics, completed, size = [], '', 0, 0
    idle, previous, last_progress = 0.0, time.monotonic(), 0.0
    auth_reported = False
    try:
        while completed < 2:
            now = time.monotonic()
            elapsed = now - previous
            previous = now
            # A suspended process tree does not consume its network idle budget.
            idle += elapsed if elapsed < 3 else 0
            if idle > idle_timeout:
                raise MediaError('network')
            try:
                error, line = channel.get(timeout=0.2)
            except queue.Empty:
                continue
            idle = 0
            if line is None:
                completed += 1
                continue
            if error:
                diagnostics = (diagnostics + line)[-524288:]
                code = error_code(line)
                if authentication and 'ERROR:' in line and code in AUTH_ERRORS and not auth_reported:
                    emit('error', code=code)
                    auth_reported = True
            else:
                size += len(line)
                if size > 32 * 1024 * 1024:
                    raise MediaError('download')
                output.append(line)
            match = re.search(r'\[download\]\s+([0-9.]+)%', line)
            if match and now - last_progress > 0.5:
                emit('progress', phase=phase, percent=min(100, max(0, float(match[1]))))
                last_progress = now
        process.wait()
        if process.returncode:
            raise MediaError(error_code(diagnostics) if authentication else 'verify' if phase == 'verify' else 'media')
        return ''.join(output)
    finally:
        kill_owned(process)
        for thread in threads:
            thread.join(timeout=3)


@contextlib.contextmanager
def file_lock(path, phase):
    import msvcrt
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('a+b') as lock:
        if not path.stat().st_size:
            lock.write(b'0')
            lock.flush()
        while True:
            try:
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
                break
            except OSError:
                emit('progress', phase=phase)
                time.sleep(1)
        try:
            yield
        finally:
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)


class Engine:
    def __init__(self, config):
        self.config = config
        self.tools = Path(config['tools_dir'])
        self.root = Path(__file__).resolve().parent

    def ytdlp(self, args, phase):
        common = [str(self.tools / 'yt-dlp.exe'), '--ignore-config', '--no-playlist',
                  '--encoding', 'utf-8', '--socket-timeout', '20', '--retries', '1',
                  '--fragment-retries', '1', '--extractor-retries', '1',
                  '--js-runtimes', 'deno:' + str(self.tools / 'deno.exe'),
                  '--ffmpeg-location', str(self.tools)]
        return run_command(common + self.config['auth_args'] + args + ['--', self.config['url']], phase, authentication=True)

    def metadata(self):
        emit('progress', phase='metadata')
        try:
            return json.loads(self.ytdlp(['--skip-download', '--dump-single-json', '--no-warnings'], 'metadata'))
        except (ValueError, TypeError):
            raise MediaError('download')

    def download(self, base, video):
        emit('progress', phase='download' if video else 'audio')
        height = self.config['height']
        selection = video_format(height) if video else 'bestaudio/best'
        args = ['--no-simulate', '--continue', '--newline', '--progress', '--no-overwrites',
                '-f', selection, '-o', str(base).replace('%', '%%') + '.%(ext)s',
                '--print', 'after_move:MEDIA_PATH:%(filepath)s']
        if video:
            args += ['--merge-output-format', 'mp4', '--remux-video', 'mp4']
        else:
            args += ['-x', '--audio-format', 'mp3', '--audio-quality', '0']
        text = self.ytdlp(args, 'download' if video else 'audio')
        names = [line[11:] for line in text.splitlines() if line.startswith('MEDIA_PATH:')]
        if not names or not Path(names[-1]).is_file() or not Path(names[-1]).resolve().is_relative_to(base.parent.resolve()):
            raise MediaError('download')
        return str(Path(names[-1]).resolve())

    def extract_audio(self, video, base):
        emit('progress', phase='extract_audio')
        target = Path(str(base) + '.mp3')
        if target.exists():
            raise MediaError('files')
        temporary = target.with_name(target.stem + '.' + uuid.uuid4().hex + '.partial.mp3')
        try:
            run_command([str(self.tools / 'ffmpeg.exe'), '-nostdin', '-v', 'error', '-n', '-i', video,
                         '-vn', '-codec:a', 'libmp3lame', '-q:a', '0', str(temporary)], 'extract_audio')
            temporary.rename(target)
        finally:
            temporary.unlink(missing_ok=True)
        return str(target)

    def subtitles(self, choice, folder):
        kind, language = choice
        emit('progress', phase='subtitles')
        for old in folder.glob('caption.*.json3'):
            old.unlink()
        args = ['--skip-download', '--no-write-subs', '--no-write-auto-subs',
                '--write-subs' if kind == 'manual' else '--write-auto-subs',
                '--sub-langs', '^' + re.escape(language) + '$', '--sub-format', 'json3',
                '-o', str(folder / 'caption').replace('%', '%%') + '.%(ext)s']
        self.ytdlp(args, 'subtitles')
        captions = list(folder.glob('caption.*.json3'))
        if len(captions) != 1:
            raise MediaError('subtitles')
        try:
            return parse_json3(json.loads(captions[0].read_text(encoding='utf-8')))
        except (ValueError, OSError):
            raise MediaError('subtitles')

    def probe(self, path, video):
        text = run_command([str(self.tools / 'ffprobe.exe'), '-v', 'error', '-show_entries',
                            'stream=codec_type,width,height:format=duration', '-of', 'json', path], 'verify')
        try:
            data = json.loads(text)
            if video:
                stream = next(s for s in data['streams'] if s.get('codec_type') == 'video')
                return f"{stream['width']} × {stream['height']}"
            if not any(s.get('codec_type') == 'audio' for s in data['streams']):
                raise MediaError('verify')
            return '音频'
        except (ValueError, KeyError, StopIteration, TypeError):
            raise MediaError('verify')

    def transcribe(self, audio, language):
        model_dir = self.root / 'models' / 'large-v3'
        if not (model_dir / 'model.bin').is_file():
            raise MediaError('missing_model')
        # Lock location is shared across relocated copies on this Windows user.
        lock = Path(os.environ.get('LOCALAPPDATA', os.environ['TEMP'])) / 'YouTube4K' / 'gpu-transcription.lock'
        with file_lock(lock, 'waiting_gpu'):
            emit('progress', phase='model')
            try:
                from gpu_runtime import configure
                configure()
                from faster_whisper import WhisperModel
                model = WhisperModel(str(model_dir), device='cuda', compute_type='float16', local_files_only=True)
                segments, info = model.transcribe(audio, language=None if language == 'auto' else language,
                                                 task='transcribe', beam_size=5, vad_filter=True,
                                                 initial_prompt='以下是普通话的简体中文转写。' if language == 'zh' else None)
                result = []
                for segment in segments:
                    text = segment.text.strip()
                    if text:
                        result.append({'start': segment.start, 'end': segment.end, 'text': text})
                    emit('progress', phase='transcribe', percent=min(100, 100 * segment.end / max(1, info.duration)))
                if not result:
                    raise MediaError('no_speech')
                return result, info.language
            except MediaError:
                raise
            except Exception:
                raise MediaError('transcription') from None


def save_result(path, result):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + '.partial')
    temporary.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    temporary.replace(path)


def verify_owned_media(engine, result, kind, manifest):
    """A rejected owned file must not be treated as a resumable success forever."""
    path = Path(result['files'][kind])
    try:
        return engine.probe(str(path), kind == 'video')
    except MediaError as exc:
        if exc.code == 'verify':
            rejected = path.with_name(path.name + '.rejected-' + uuid.uuid4().hex[:10])
            path.rename(rejected)
            del result['files'][kind]
            save_result(manifest, result)
        raise


def run(config, engine=None):
    destination = Path(config['destination']).resolve()
    destination.mkdir(parents=True, exist_ok=True)
    wanted = set(config['outputs'])
    if not wanted or not wanted <= {'video', 'audio', 'transcript'}:
        raise MediaError('configuration')
    if not re.fullmatch(r'[A-Za-z0-9_-]{11}', config['video_id']):
        raise MediaError('configuration')
    if config['language'] not in ('auto', 'zh', 'en', 'ja', 'ko') or config['source'] not in ('auto', 'whisper'):
        raise MediaError('configuration')
    result = {'job_key': config['job_key'], 'files': {}, 'status': 'running', 'resolution': '', 'source': '', 'language': ''}
    try:
        prior = json.loads(Path(config['result_path']).read_text(encoding='utf-8'))
        if prior.get('job_key') == config['job_key']:
            for key, value in prior.get('files', {}).items():
                if key in ('video', 'audio', 'txt', 'md') and Path(value).is_file() and Path(value).stat().st_size > 0 and Path(value).resolve().is_relative_to(destination):
                    result['files'][key] = value
            for key in ('base', 'title', 'resolution', 'source', 'language'):
                if key in prior:
                    result[key] = prior[key]
    except (OSError, ValueError, TypeError):
        pass
    engine = engine or Engine(config)
    scratch = destination / '.yt-media' / hashlib.sha256(config['job_key'].encode()).hexdigest()[:20]
    scratch.mkdir(parents=True, exist_ok=True)
    try:
        info = engine.metadata()
        title = str(info.get('title') or config['video_id'])
        emit('title', text=title.replace('\n', ' ')[:300])
        # A separate stable namespace avoids sharing .part/merge files with the
        # unchanged video-only branch, even when both jobs run concurrently.
        task_suffix = '_素材_' + hashlib.sha256(config['job_key'].encode()).hexdigest()[:10] + config.get('suffix', '')
        base = destination / file_stem(title, config['video_id'], task_suffix)
        lock_name = hashlib.sha256(str(base).casefold().encode()).hexdigest()[:24] + '.lock'
        with file_lock(destination / '.yt-media' / lock_name, 'waiting_file'):
            if result.get('base') and Path(result['base']).resolve().parent == destination:
                base = Path(result['base'])
            elif any(Path(str(base) + ext).exists() for ext in ('.mp4', '.mp3', '.txt', '.md')):
                base = Path(str(base) + '_' + hashlib.sha256(config['job_key'].encode()).hexdigest()[:10])
            result.update(base=str(base), title=title)
            save_result(config['result_path'], result)
            files = result['files']
            if 'video' in wanted and 'video' not in files:
                files['video'] = engine.download(base, True)
                save_result(config['result_path'], result)
            if 'video' in files:
                result['resolution'] = verify_owned_media(engine, result, 'video', config['result_path'])
                save_result(config['result_path'], result)
            if 'audio' in wanted and 'audio' not in files:
                files['audio'] = engine.extract_audio(files['video'], base) if 'video' in files else engine.download(base, False)
                save_result(config['result_path'], result)
            if 'audio' in files:
                verify_owned_media(engine, result, 'audio', config['result_path'])
            if 'transcript' in wanted and not all(key in files for key in ('txt', 'md')):
                choice = choose_subtitle(info, config['language']) if config['source'] == 'auto' else None
                segments, source, language = [], '', ''
                if choice:
                    try:
                        segments = engine.subtitles(choice, scratch)
                        source = '作者字幕' if choice[0] == 'manual' else 'YouTube 自动字幕'
                        language = choice[1].removesuffix('-orig')
                    except MediaError as exc:
                        if exc.code in AUTH_ERRORS:
                            raise
                        emit('progress', phase='subtitle_fallback')
                if not segments:
                    audio = files.get('audio') or files.get('video')
                    if not audio:
                        audio = engine.download(scratch / 'speech', False)
                        try:
                            engine.probe(audio, False)
                        except MediaError as exc:
                            if exc.code == 'verify':
                                path = Path(audio)
                                path.rename(path.with_name(path.name + '.rejected-' + uuid.uuid4().hex[:10]))
                            raise
                    segments, language = engine.transcribe(audio, config['language'])
                    source = 'Whisper 本地语音识别'
                files.update(export_transcript(base, title, segments, source, language))
                result.update(source=source, language=language)
                save_result(config['result_path'], result)
            result['status'] = 'completed'
            save_result(config['result_path'], result)
        # Only our completed scratch files are removed; partial media remain for retries.
        for file in scratch.iterdir():
            if file.is_file():
                file.unlink()
        scratch.rmdir()
        emit('done')
        return 0
    except Exception as exc:
        code = exc.code if isinstance(exc, MediaError) else 'files' if isinstance(exc, OSError) else 'download'
        result.update(status='failed', error=code)
        save_result(config['result_path'], result)
        emit('error', code=code)
        return 1


if __name__ == '__main__':
    try:
        configuration = json.loads(sys.stdin.read())
        sys.exit(run(configuration))
    except Exception:
        emit('error', code='configuration')
        sys.exit(1)
