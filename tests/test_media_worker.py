import importlib.util
import io
import json
import tempfile
import unittest
from unittest import mock
from pathlib import Path


class AvailabilityTests(unittest.TestCase):
    def test_worker_can_be_loaded_without_loading_gpu(self):
        self.assertIsNotNone(importlib.util.find_spec('media_worker'), 'missing unified media worker')


class ContentTests(unittest.TestCase):
    def setUp(self):
        import media_worker
        self.m = media_worker

    def test_unicode_event_survives_non_utf8_windows_console(self):
        stream = io.TextIOWrapper(io.BytesIO(), encoding='cp1252', errors='strict')
        with mock.patch('sys.stdout', stream):
            self.m.emit('title', text='中文标题')
        stream.seek(0)
        event = json.loads(stream.read().removeprefix('@@MEDIA '))
        self.assertEqual(event['text'], '中文标题')

    def track(self, lang, translated=False):
        return [{'ext': 'json3', 'url': 'https://example.invalid/sub?lang=' + lang + ('&tlang=zh' if translated else '')}]

    def test_original_manual_subtitles_preferred_over_translated_and_auto(self):
        info = {'language': 'en', 'subtitles': {'zh': self.track('en', True), 'en': self.track('en')},
                'automatic_captions': {'en-orig': self.track('en')}}
        self.assertEqual(self.m.choose_subtitle(info, 'auto'), ('manual', 'en'))

    def test_unknown_original_uses_orig_track_and_never_arbitrary_translation(self):
        info = {'subtitles': {'zh': self.track('en', True)},
                'automatic_captions': {'en-orig': self.track('en'), 'zh': self.track('en', True)}}
        self.assertEqual(self.m.choose_subtitle(info, 'auto'), ('automatic', 'en-orig'))
        self.assertIsNone(self.m.choose_subtitle({'subtitles': {'zh': self.track('en', True)}}, 'auto'))

    def test_explicit_original_language_can_select_manual_track_when_metadata_omits_language(self):
        info = {'subtitles': {'en': self.track('en'), 'zh': self.track('en', True)}}
        self.assertEqual(self.m.choose_subtitle(info, 'en'), ('manual', 'en'))

    def test_json3_rollup_dedup_keeps_intentionally_repeated_words(self):
        data = {'events': [
            {'tStartMs': 0, 'dDurationMs': 2000, 'segs': [{'utf8': '你好世界'}]},
            {'tStartMs': 1000, 'dDurationMs': 2000, 'segs': [{'utf8': '世界再见'}]},
            {'tStartMs': 4000, 'dDurationMs': 1000, 'segs': [{'utf8': '再见'}]},
            {'tStartMs': 5000, 'dDurationMs': 1000, 'segs': [{'utf8': '\n'}]},
        ]}
        segments = self.m.parse_json3(data)
        self.assertEqual([s['text'] for s in segments], ['你好世界', '再见', '再见'])
        self.assertEqual((segments[1]['start'], segments[1]['end']), (1.0, 3.0))

    def test_output_preserves_unicode_and_does_not_overwrite_user_files(self):
        with tempfile.TemporaryDirectory(prefix='文稿 & ') as folder:
            base = Path(folder) / '摆原木找平 [abcdefghijk]'
            result = self.m.export_transcript(base, '摆原木找平', [{'start': 26, 'end': 35, 'text': '这是原话。'}], '作者字幕', 'zh')
            self.assertEqual(Path(result['txt']).read_text(encoding='utf-8-sig'), '这是原话。\n')
            text = Path(result['md']).read_text(encoding='utf-8-sig')
            self.assertIn('00:00:26', text)
            self.assertIn('作者字幕', text)
            with self.assertRaises(FileExistsError):
                self.m.export_transcript(base, 'different', [{'start': 0, 'end': 1, 'text': 'changed'}], 'Whisper', 'en')
            self.assertIn('这是原话', Path(result['txt']).read_text(encoding='utf-8-sig'))

    def test_three_cumulative_caption_windows_do_not_repeat_earlier_words(self):
        data = {'events': [
            {'tStartMs': 0, 'dDurationMs': 5000, 'segs': [{'utf8': 'one two'}]},
            {'tStartMs': 1000, 'dDurationMs': 5000, 'segs': [{'utf8': 'one two three'}]},
            {'tStartMs': 2000, 'dDurationMs': 5000, 'segs': [{'utf8': 'one two three four'}]},
        ]}
        self.assertEqual([s['text'] for s in self.m.parse_json3(data)], ['one two', 'three', 'four'])

    def test_filename_blocks_windows_paths_and_keeps_video_identity(self):
        name = self.m.file_stem('..\\秘密/中文:标题?*\n', 'abcdefghijk', '')
        self.assertNotIn('\\', name)
        self.assertNotIn('/', name)
        self.assertNotIn(':', name)
        self.assertIn('中文', name)
        self.assertTrue(name.endswith('[abcdefghijk]'))

    def test_errors_are_fixed_codes_without_secret_content(self):
        self.assertEqual(self.m.error_code('ERROR: Sign in to confirm you are not a bot. SID=SECRET'), 'authentication')
        self.assertEqual(self.m.error_code('ERROR: Could not copy Chrome cookie database SECRET'), 'cookies_locked')
        self.assertEqual(self.m.error_code('ERROR: DPAPI failed SECRET'), 'decrypt')
        self.assertEqual(self.m.error_code('ERROR: Video unavailable SECRET'), 'unavailable')
        self.assertEqual(self.m.error_code('ERROR: secret_token=XYZ unknown'), 'download')

    def test_high_resolution_selection_has_8k_and_6k_caps(self):
        self.assertTrue(hasattr(self.m, 'video_format'), 'shared media format builder missing')
        self.assertEqual(self.m.video_format(4320), 'bestvideo[height<=4320][width<=7680]+bestaudio/best[height<=4320][width<=7680]')
        self.assertEqual(self.m.video_format(3456), 'bestvideo[height<=3456][width<=6144]+bestaudio/best[height<=3456][width<=6144]')
        self.assertEqual(self.m.video_format(2160), 'bestvideo[height<=2160]+bestaudio/best[height<=2160]')

    def test_empty_or_malformed_subtitles_never_become_success(self):
        self.assertEqual(self.m.parse_json3({'events': []}), [])
        self.assertEqual(self.m.parse_json3({'events': [{'tStartMs': -1, 'segs': [{'utf8': 'bad'}]}]}), [])
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(ValueError):
                self.m.export_transcript(Path(folder) / 'empty', 'empty', [], '字幕', 'en')
            self.assertFalse((Path(folder) / 'empty.txt').exists())


class PipelineTests(unittest.TestCase):
    def setUp(self):
        import media_worker
        self.m = media_worker
        self.temp = tempfile.TemporaryDirectory(prefix='素材测试 & ')
        self.root = Path(self.temp.name)
        self.config = {'destination': str(self.root), 'outputs': ['transcript'], 'height': 720,
                       'source': 'auto', 'language': 'auto', 'video_id': 'abcdefghijk',
                       'job_key': 'task-1', 'result_path': str(self.root / 'result.json')}
        self.engine = mock.Mock(spec=self.m.Engine)
        self.engine.metadata.return_value = {'title': '中文测试', 'language': 'en',
              'subtitles': {'en': [{'ext': 'json3', 'url': 'https://example.invalid/sub?lang=en'}]}}
        self.engine.subtitles.return_value = [{'start': 0, 'end': 2, 'text': 'Hello, this is the original.'}]
        self.engine.probe.return_value = '1280 × 720'
        self.engine.transcribe.return_value = ([{'start': 1, 'end': 3, 'text': 'Local original speech.'}], 'en')
        def download(base, video):
            target = Path(str(base) + ('.mp4' if video else '.mp3'))
            target.write_bytes(b'fixture media')
            return str(target)
        self.engine.download.side_effect = download
        self.engine.extract_audio.side_effect = lambda video, base: download(base, False)

    def tearDown(self):
        self.temp.cleanup()

    def result(self):
        return json.loads(Path(self.config['result_path']).read_text(encoding='utf-8'))

    def test_subtitle_only_job_produces_text_without_media_or_gpu(self):
        self.assertEqual(self.m.run(self.config, self.engine), 0)
        value = self.result()
        self.assertEqual(value['source'], '作者字幕')
        self.assertEqual(set(value['files']), {'txt', 'md'})
        self.assertIn('Hello', Path(value['files']['txt']).read_text(encoding='utf-8-sig'))
        self.engine.download.assert_not_called()
        self.engine.transcribe.assert_not_called()

    def test_auth_error_in_subtitles_stops_without_download_or_transcription(self):
        self.engine.subtitles.side_effect = self.m.MediaError('authentication')
        self.assertEqual(self.m.run(self.config, self.engine), 1)
        self.assertEqual(self.result()['error'], 'authentication')
        self.assertFalse(list(self.root.glob('*.txt')))
        self.engine.download.assert_not_called()
        self.engine.transcribe.assert_not_called()

    def test_absent_subtitles_downloads_only_temporary_audio_then_cleans_it(self):
        self.engine.metadata.return_value = {'title': '原声', 'language': 'en'}
        self.assertEqual(self.m.run(self.config, self.engine), 0)
        self.assertEqual(self.result()['source'], 'Whisper 本地语音识别')
        self.assertEqual(self.result()['language'], 'en')
        self.assertFalse(list(self.root.rglob('*.mp3')))
        self.assertIn('Local original speech', Path(self.result()['files']['txt']).read_text(encoding='utf-8-sig'))

    def test_combined_outputs_reuse_video_for_audio_and_preserve_partial_success(self):
        self.config.update(outputs=['video', 'audio', 'transcript'], source='whisper')
        self.engine.transcribe.side_effect = self.m.MediaError('transcription')
        self.assertEqual(self.m.run(self.config, self.engine), 1)
        before = self.result()
        self.assertTrue(Path(before['files']['video']).exists())
        self.assertTrue(Path(before['files']['audio']).exists())
        self.assertNotIn('txt', before['files'])
        video = Path(before['files']['video'])
        video.write_bytes(b'preserved original')
        self.engine.transcribe.side_effect = None
        self.assertEqual(self.m.run(self.config, self.engine), 0)
        self.assertEqual(video.read_bytes(), b'preserved original')
        self.assertEqual(set(self.result()['files']), {'video', 'audio', 'txt', 'md'})
        self.assertEqual(self.engine.download.call_count, 1)

    def test_unowned_same_name_is_preserved_and_new_job_gets_separate_outputs(self):
        existing = self.root / '中文测试 [abcdefghijk].txt'
        existing.write_text('用户原有文字', encoding='utf-8')
        self.assertEqual(self.m.run(self.config, self.engine), 0)
        self.assertEqual(existing.read_text(encoding='utf-8'), '用户原有文字')
        self.assertNotEqual(self.result()['files']['txt'], str(existing))

    def test_enhanced_media_does_not_share_legacy_inflight_output_template(self):
        legacy = self.root / '中文测试 [abcdefghijk].mp4.part'
        legacy.write_bytes(b'legacy download in progress')
        self.config['outputs'] = ['video', 'audio']
        self.assertEqual(self.m.run(self.config, self.engine), 0)
        self.assertNotEqual(Path(self.result()['files']['video'] + '.part').resolve(), legacy.resolve())
        self.assertEqual(legacy.read_bytes(), b'legacy download in progress')

    def test_failed_media_verification_is_quarantined_and_retry_downloads_fresh_file(self):
        self.config['outputs'] = ['audio']
        self.engine.probe.side_effect = self.m.MediaError('verify')
        self.assertEqual(self.m.run(self.config, self.engine), 1)
        self.assertNotIn('audio', self.result()['files'])
        self.assertTrue(list(self.root.glob('*.rejected-*')))
        self.engine.probe.side_effect = None
        self.assertEqual(self.m.run(self.config, self.engine), 0)
        self.assertEqual(self.engine.download.call_count, 2)
        self.assertTrue(Path(self.result()['files']['audio']).is_file())

    def test_invalid_temporary_transcription_audio_is_quarantined_before_whisper_and_retry(self):
        self.engine.metadata.return_value = {'title': '临时音频', 'language': 'en'}
        self.engine.probe.side_effect = self.m.MediaError('verify')
        self.assertEqual(self.m.run(self.config, self.engine), 1)
        self.engine.transcribe.assert_not_called()
        self.assertTrue(list(self.root.rglob('speech.mp3.rejected-*')))
        self.engine.probe.side_effect = None
        self.assertEqual(self.m.run(self.config, self.engine), 0)
        self.assertEqual(self.engine.download.call_count, 2)
        self.assertTrue(Path(self.result()['files']['txt']).is_file())


if __name__ == '__main__':
    unittest.main()
