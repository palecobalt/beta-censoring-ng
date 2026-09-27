class CensorError(Exception):
    """A clip that can't be censored; status is the HTTP status to answer with."""

    status = 422


class NoVideo(CensorError):
    """The file has no video stream, e.g. audio served as video/webm."""


class TooLarge(CensorError):
    """The clip is over the size or duration limit."""

    status = 413


class DetectionFailed(CensorError):
    """Beta Censoring couldn't be reached or returned an error."""

    status = 502


class EncodeFailed(CensorError):
    status = 500
