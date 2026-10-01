"""The add-on itself, with mitmproxy's test flows and a stand-in for the video service."""
import asyncio
import os
import sys
import time
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

try:
    from mitmproxy.test import tflow
except ImportError:  # the rule modules are tested without mitmproxy
    tflow = None

if tflow:
    import censor_proxy  # noqa: E402

CLIP = b"\x00" * 5000
URL = "http://media.example/clips/a.mp4"


@unittest.skipUnless(tflow, "needs mitmproxy")
class SameClipTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.addon = censor_proxy.CensorProxy()
        # as the real call to the video service does first
        self.addon._censor_options()
        self.posts = 0
        self.fail_with = None

        def censor_clip(body, content_type):
            self.posts += 1
            time.sleep(0.2)
            if self.fail_with:
                raise RuntimeError(self.fail_with)
            return b"censored:" + body[:4], True

        self.addon._censor_clip = censor_clip

    @staticmethod
    def flow(range_header=None):
        flow = tflow.tflow(resp=True)
        flow.request.url = URL
        flow.request.headers["sec-fetch-dest"] = "video"
        if range_header:
            flow.request.headers["range"] = range_header
        flow.response.headers["content-type"] = "video/mp4"
        flow.response.content = CLIP
        return flow

    async def fetch(self, flow):
        """What mitmproxy does with a flow whose request the add-on let through."""
        self.addon.responseheaders(flow)
        await self.addon.response(flow)
        return flow

    async def test_two_requests_at_once_censor_the_clip_once(self):
        first, second = self.flow(), self.flow()
        await asyncio.gather(self.fetch(first), self.fetch(second))

        self.assertEqual(self.posts, 1)
        for flow in (first, second):
            self.assertEqual(flow.response.status_code, 200)
            self.assertEqual(flow.response.content, b"censored:" + CLIP[:4])
            self.assertEqual(flow.response.headers["x-censored"], "1")
            self.assertEqual(flow.response.headers["content-type"], "video/mp4")

    async def test_a_request_arriving_during_the_run_is_answered_without_fetching(self):
        first = asyncio.create_task(self.fetch(self.flow()))
        await asyncio.sleep(0.05)
        late = tflow.tflow()
        late.request.url = URL
        late.request.headers["range"] = "bytes=9-"
        await self.addon.request(late)
        await first

        self.assertEqual(self.posts, 1)
        self.assertEqual(late.response.status_code, 206)
        self.assertEqual(late.response.content, CLIP[:4])
        self.assertEqual(late.response.headers["content-range"], "bytes 9-12/13")
        # and afterwards from the cache
        again = tflow.tflow()
        again.request.url = URL
        await self.addon.request(again)
        self.assertEqual(again.response.content, b"censored:" + CLIP[:4])
        self.assertEqual(self.posts, 1)

    async def test_a_failure_reaches_every_request_for_the_clip(self):
        self.fail_with = "video service returned 500"
        first, second = self.flow(), self.flow()
        running = asyncio.gather(self.fetch(first), self.fetch(second))
        await asyncio.sleep(0.05)
        late = tflow.tflow()
        late.request.url = URL
        await self.addon.request(late)
        await running

        self.assertEqual(self.posts, 1)
        for flow in (first, second, late):
            self.assertEqual(flow.response.status_code, 403)
            self.assertEqual(flow.response.headers["x-censored"], "blocked")
            self.assertEqual(flow.response.content, b"")
        # nothing is remembered about the failure: the next request tries again
        self.fail_with = None
        retry = await self.fetch(self.flow())
        self.assertEqual(self.posts, 2)
        self.assertEqual(retry.response.headers["x-censored"], "1")

    async def test_a_waiting_request_takes_over_when_the_first_one_is_abandoned(self):
        abandoned = self.flow()
        self.addon.responseheaders(abandoned)
        late = tflow.tflow()
        late.request.url = URL
        waiting = asyncio.create_task(self.addon.request(late))
        await asyncio.sleep(0.05)
        # the first browser request is dropped while the clip is still downloading
        self.addon.error(abandoned)
        await waiting

        self.assertIsNone(late.response)
        late.response = self.flow().response
        await self.fetch(late)
        self.assertEqual(self.posts, 1)
        self.assertEqual(late.response.headers["x-censored"], "1")

    async def test_the_run_goes_on_for_the_others_when_its_own_browser_gives_up(self):
        first = self.flow()
        running = asyncio.create_task(self.fetch(first))
        await asyncio.sleep(0.05)
        late = tflow.tflow()
        late.request.url = URL
        waiting = asyncio.create_task(self.addon.request(late))
        await asyncio.sleep(0.05)
        self.addon.error(first)
        await asyncio.gather(running, waiting)

        self.assertEqual(self.posts, 1)
        self.assertEqual(late.response.content, b"censored:" + CLIP[:4])
