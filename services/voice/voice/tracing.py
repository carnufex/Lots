"""Trace context for the voice service (#139): requests from the shell carry W3C `traceparent`, so speech work belongs to the run's
trace. With OTEL_EXPORTER_OTLP_ENDPOINT set (and the SDK installed, as in the image) every request is a server span exported over
OTLP; without it the incoming context is still adopted, so log lines carry the run's trace id.
"""
from __future__ import annotations

import os
from contextlib import contextmanager

try:
    from opentelemetry import context as otel_context
    from opentelemetry import trace
    from opentelemetry.trace.propagation.tracecontext import TraceContextTextMapPropagator
except ImportError:  # the API is optional for local runs without the engines
    trace = None

_propagator = TraceContextTextMapPropagator() if trace else None
_tracer = None


def configure() -> None:
    """Sets up the exporter once, when an endpoint is configured and the SDK is present."""
    global _tracer
    if trace is None:
        return
    if os.environ.get("OTEL_EXPORTER_OTLP_ENDPOINT"):
        try:
            from opentelemetry.exporter.otlp.proto.http.trace_exporter import OTLPSpanExporter
            from opentelemetry.sdk.resources import Resource
            from opentelemetry.sdk.trace import TracerProvider
            from opentelemetry.sdk.trace.export import BatchSpanProcessor

            provider = TracerProvider(resource=Resource.create({"service.name": "lots-voice"}))
            provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter()))
            trace.set_tracer_provider(provider)
        except ImportError:
            pass
    _tracer = trace.get_tracer("lots-voice")


@contextmanager
def request_span(name: str, headers: dict[str, str]):
    """A server span under the caller's trace (or just the caller's context when nothing is exported)."""
    if trace is None:
        yield None
        return
    parent = _propagator.extract(headers)
    token = otel_context.attach(parent)
    try:
        with (_tracer or trace.get_tracer("lots-voice")).start_as_current_span(name, kind=trace.SpanKind.SERVER) as span:
            yield span
    finally:
        otel_context.detach(token)


def current_ids() -> tuple[str, str] | None:
    """(trace_id, span_id) of the active span, for log lines."""
    if trace is None:
        return None
    ctx = trace.get_current_span().get_span_context()
    if not ctx.is_valid:
        return None
    return format(ctx.trace_id, "032x"), format(ctx.span_id, "016x")
