"""Process-local network deny guard for the offline Foundation Worker."""

from __future__ import annotations

import socket


class NetworkAccessDenied(OSError):
    pass


def install() -> None:
    original_socket = socket.socket

    class OfflineSocket(original_socket):
        def connect(self, address):  # type: ignore[no-untyped-def]
            raise NetworkAccessDenied("Foundation Worker network access is disabled.")

        def connect_ex(self, address):  # type: ignore[no-untyped-def]
            raise NetworkAccessDenied("Foundation Worker network access is disabled.")

    def denied_create_connection(*args, **kwargs):  # type: ignore[no-untyped-def]
        raise NetworkAccessDenied("Foundation Worker network access is disabled.")

    socket.socket = OfflineSocket
    socket.create_connection = denied_create_connection

