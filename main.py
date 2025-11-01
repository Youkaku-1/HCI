#!/usr/bin/env python3
"""
Enhanced TUIO Broadcaster for Patient Medication System

Listens for TUIO objects from reacTIVision on UDP port (3333)
Accepts plain TCP client connections on localhost:8765
Broadcasts newline-delimited JSON messages to all connected clients

Dependencies:
  pip install python-tuio

Run:
  python tuio_broadcaster_local_sockets.py
"""

import socket
import threading
import json
import math
import time
import signal
import sys
import logging
from typing import Dict, Any

# ====== TUIO IMPORT ======
try:
    from pythontuio import TuioClient, TuioListener

    TUIO_AVAILABLE = True
    print("Successfully imported pythontuio")
except ImportError as e:
    print(f"pythontuio import failed: {e}")
    TUIO_AVAILABLE = False

# ====== CONFIG ======
TUIO_ADDR = ("0.0.0.0", 3333)
TCP_HOST = "127.0.0.1"
TCP_PORT = 8765

# Symbol mappings - 0=rotate, 1=select
ROTATE_SYMBOL = 0  # Fiducial ID 0 for rotation
SELECT_SYMBOL = 1  # Fiducial ID 1 for selection
BACK_SYMBOL = 12
MEDICATION_SYMBOLS = {
    2: "Paracetamol",
    3: "Amoxicillin",
    4: "Aspirin",
    5: "Metformin",
    6: "Lisinopril",
    7: "Atorvastatin",
    8: "Omeprazole",
    9: "Salbutamol",
    10: "Ibuprofen",
    11: "Vitamin D"
}

NUM_WHEEL_SECTORS = len(MEDICATION_SYMBOLS)
PROXIMITY_THRESHOLD = 0.08
# ====================

# Global variables
clients = []
clients_lock = threading.Lock()
latest_objects: Dict[int, Dict[str, Any]] = {}
latest_objects_lock = threading.Lock()
running = True

# Setup logging
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)


def broadcast(obj: Dict[str, Any]):
    """Serialize and send to all connected clients (newline-delimited)."""
    data = (json.dumps(obj) + "\n").encode("utf-8")
    with clients_lock:
        dead = []
        for client_socket in clients:
            try:
                client_socket.sendall(data)
            except Exception:
                dead.append(client_socket)
        for dead_client in dead:
            clients.remove(dead_client)


# ====== PROPER TUIO LISTENER IMPLEMENTATION ======
class MyTuioListener(TuioListener):
    def __init__(self):
        super().__init__()

    def add_tuio_object(self, obj):
        self._handle_obj("add", obj)

    def update_tuio_object(self, obj):
        self._handle_obj("update", obj)

    def remove_tuio_object(self, obj):
        self._handle_obj("remove", obj)

    def refresh(self, time):
        pass

    def _handle_obj(self, event_type, obj):
        try:
            # Try different possible attribute names for the marker ID
            symbol_id = None

            # Try all possible attribute names for the fiducial ID
            possible_id_attrs = ['fiducial_id', 'symbol_id', 'class_id', 'pattern_id']
            for attr in possible_id_attrs:
                if hasattr(obj, attr):
                    symbol_id = getattr(obj, attr)
                    break

            if symbol_id is None:
                # If we still can't find it, log all available attributes
                logger.warning(f"Could not find marker ID in object. Available attributes: {dir(obj)}")
                return

            session_id = getattr(obj, 'session_id', None)

            # Try different possible attribute names for position
            x = getattr(obj, 'x', getattr(obj, 'xpos', getattr(obj, 'x_pos', 0.5)))
            y = getattr(obj, 'y', getattr(obj, 'ypos', getattr(obj, 'y_pos', 0.5)))
            angle = getattr(obj, 'angle', 0)

            record = {
                "event": event_type,
                "symbol_id": symbol_id,
                "session_id": session_id,
                "x": x,
                "y": y,
                "angle": angle,
            }

            logger.info(
                f"TUIO {event_type}: fiducial={symbol_id}, session={session_id}, x={x:.3f}, y={y:.3f}, angle={angle:.3f}")

            # Update cache
            with latest_objects_lock:
                if event_type in ("add", "update"):
                    latest_objects[session_id] = record
                else:
                    latest_objects.pop(session_id, None)

            # Process logic
            process_logic_and_broadcast(record)

        except Exception as e:
            logger.error(f"Error handling TUIO object: {e}")


def process_logic_and_broadcast(record: Dict[str, Any]):
    """Process TUIO object events and broadcast appropriate messages."""
    sid = record["symbol_id"]
    evt = record["event"]

    # Always broadcast raw object for debugging
    broadcast({"type": "tuio_obj", "payload": record})

    # Medication selection detected directly
    if sid in MEDICATION_SYMBOLS and evt == "add":
        med_name = MEDICATION_SYMBOLS[sid]
        logger.info(f"Direct medication selection: {med_name} (fiducial {sid})")
        broadcast({
            "type": "medication_selected",
            "medication": med_name,
            "symbol_id": sid
        })

    # Wheel logic for rotate marker (fiducial ID 0)
    if sid == ROTATE_SYMBOL and evt == "add":
        logger.info("Rotate marker (fiducial 0) detected - opening wheel")
        broadcast({"type": "wheel_open", "x": record["x"], "y": record["y"]})

    if sid == ROTATE_SYMBOL and evt in ("add", "update"):
        # Calculate wheel sector based on rotation angle
        theta = record["angle"] % (2 * math.pi)
        frac = theta / (2 * math.pi)
        sector = int(frac * NUM_WHEEL_SECTORS) % NUM_WHEEL_SECTORS

        # Get medication name for this sector
        medication_name = list(MEDICATION_SYMBOLS.values())[sector]

        logger.info(f"Rotate marker at angle {theta:.2f} -> sector {sector} ({medication_name})")

        broadcast({
            "type": "wheel_hover",
            "sector": sector,
            "angle": theta,
            "x": record["x"],
            "y": record["y"],
            "medication": medication_name
        })

        # Check for select marker proximity (fiducial ID 1)
        with latest_objects_lock:
            for session_id, srec in list(latest_objects.items()):
                if srec["symbol_id"] == SELECT_SYMBOL:
                    dx = srec["x"] - record["x"]
                    dy = srec["y"] - record["y"]
                    dist = math.hypot(dx, dy)
                    if dist < PROXIMITY_THRESHOLD:
                        selected_med = list(MEDICATION_SYMBOLS.values())[sector]
                        logger.info(f"Selection confirmed by fiducial 1: {selected_med} (sector {sector})")
                        broadcast({
                            "type": "wheel_select_confirm",
                            "sector": sector,
                            "medication": selected_med
                        })

    # Back marker pressed
    if sid == BACK_SYMBOL and evt == "add":
        logger.info("Back marker (fiducial 12) pressed")
        broadcast({"type": "back_pressed"})


# ====== SIMULATION MODE ======
class SimulatedTuioObject:
    def __init__(self, symbol_id, session_id, x=0.5, y=0.5, angle=0):
        # Set multiple possible attribute names for compatibility
        self.fiducial_id = symbol_id
        self.symbol_id = symbol_id
        self.class_id = symbol_id
        self.pattern_id = symbol_id
        self.session_id = session_id
        self.x_pos = x
        self.y_pos = y
        self.x = x
        self.y = y
        self.angle = angle


class SimulationManager:
    def __init__(self, tuio_listener):
        self.listener = tuio_listener
        self.simulation_thread = None

    def start_simulation(self):
        """Start simulation in a separate thread"""
        self.simulation_thread = threading.Thread(target=self._run_simulation, daemon=True)
        self.simulation_thread.start()

    def _run_simulation(self):
        """Run simulation sequence"""
        logger.info("Starting simulation in 3 seconds...")
        time.sleep(3)

        session_counter = 1000

        # Test 1: Rotate marker (fiducial 0) appears
        logger.info("SIMULATION: Rotate marker (fiducial 0) appears")
        obj = SimulatedTuioObject(ROTATE_SYMBOL, session_counter, 0.5, 0.5, 0)
        self.listener.add_tuio_object(obj)
        time.sleep(2)

        # Test 2: Rotate marker rotates through sectors
        angles = [0, 0.78, 1.57, 2.35, 3.14, 3.92, 4.71, 5.49]
        for i, angle in enumerate(angles):
            logger.info(f"SIMULATION: Rotate marker at angle {angle:.2f} (sector {i})")
            obj = SimulatedTuioObject(ROTATE_SYMBOL, session_counter, 0.5, 0.5, angle)
            self.listener.update_tuio_object(obj)
            time.sleep(1)

        # Test 3: Select marker (fiducial 1) appears (confirms selection)
        logger.info("SIMULATION: Select marker (fiducial 1) appears (confirms selection)")
        obj2 = SimulatedTuioObject(SELECT_SYMBOL, session_counter + 1, 0.52, 0.52)
        self.listener.add_tuio_object(obj2)
        time.sleep(2)

        # Test 4: Markers disappear
        logger.info("SIMULATION: Markers disappear")
        self.listener.remove_tuio_object(obj)
        self.listener.remove_tuio_object(obj2)
        time.sleep(2)

        # Test 5: Direct medication selection
        logger.info("SIMULATION: Direct medication selection (Aspirin - fiducial 4)")
        obj3 = SimulatedTuioObject(4, session_counter + 2, 0.3, 0.3)
        self.listener.add_tuio_object(obj3)
        time.sleep(2)
        self.listener.remove_tuio_object(obj3)

        logger.info("Simulation complete. Waiting for real TUIO events...")


def client_reader_thread(conn: socket.socket, addr):
    """Handle client connections."""
    client_name = f"{addr[0]}:{addr[1]}"
    logger.info(f"Client connected: {client_name}")

    try:
        conn.settimeout(0.5)
        while running:
            try:
                data = conn.recv(1024)
                if not data:
                    break
            except socket.timeout:
                continue
            except Exception:
                break
    except Exception as e:
        logger.error(f"Error in client thread: {e}")
    finally:
        with clients_lock:
            if conn in clients:
                clients.remove(conn)
        try:
            conn.close()
        except:
            pass
        logger.info(f"Client disconnected: {client_name}")


def tcp_acceptor(host: str, port: int):
    """Accept TCP client connections."""
    logger.info(f"Starting TCP server on {host}:{port}")

    server_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server_sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server_sock.bind((host, port))
    server_sock.listen(8)
    server_sock.settimeout(1.0)

    try:
        while running:
            try:
                conn, addr = server_sock.accept()
            except socket.timeout:
                continue
            except OSError:
                break

            with clients_lock:
                clients.append(conn)

            client_thread = threading.Thread(target=client_reader_thread, args=(conn, addr), daemon=True)
            client_thread.start()

    finally:
        server_sock.close()


def start_tuio_client(tuio_addr):
    """Start the TUIO client."""
    try:
        client = TuioClient(tuio_addr)
        listener = MyTuioListener()
        client.add_listener(listener)

        logger.info(f"✓ TUIO Client started on {tuio_addr}")

        # Start simulation if no clients after a while
        def start_simulation_if_needed():
            time.sleep(5)
            with clients_lock:
                if len(clients) == 0 and running:
                    logger.info("No clients connected, starting simulation...")
                    simulator = SimulationManager(listener)
                    simulator.start_simulation()

        simulation_thread = threading.Thread(target=start_simulation_if_needed, daemon=True)
        simulation_thread.start()

        # This blocks until client stops
        client.start()

    except Exception as e:
        logger.error(f"✗ TUIO client error: {e}")


def shutdown(signum=None, frame=None):
    """Clean shutdown handler."""
    global running
    logger.info("Shutting down...")
    running = False

    with clients_lock:
        for client_socket in list(clients):
            try:
                client_socket.close()
            except:
                pass
        clients.clear()

    time.sleep(0.3)
    sys.exit(0)


def print_usage():
    """Print usage information."""
    print("\n" + "=" * 60)
    print("TUIO Broadcaster for Patient Medication System")
    print("=" * 60)
    print(f"TUIO Listening: UDP {TUIO_ADDR[0]}:{TUIO_ADDR[1]}")
    print(f"Client Connections: TCP {TCP_HOST}:{TCP_PORT}")
    print(f"TUIO Available: {TUIO_AVAILABLE}")
    print("\nMarker Configuration:")
    print(f"  Rotate: Fiducial ID {ROTATE_SYMBOL}")
    print(f"  Select: Fiducial ID {SELECT_SYMBOL}")
    print(f"  Back: Fiducial ID {BACK_SYMBOL}")
    print("  Medications:")
    for sym_id, med_name in MEDICATION_SYMBOLS.items():
        print(f"    Fiducial ID {sym_id}: {med_name}")
    print("\nPress Ctrl+C to stop")
    print("=" * 60 + "\n")


def main():
    """Main application entry point."""
    if not TUIO_AVAILABLE:
        print("ERROR: pythontuio library not available.")
        print("Please install it with: pip install python-tuio")
        sys.exit(1)

    signal.signal(signal.SIGINT, shutdown)
    signal.signal(signal.SIGTERM, shutdown)

    print_usage()

    # Start TCP acceptor thread
    tcp_thread = threading.Thread(target=tcp_acceptor, args=(TCP_HOST, TCP_PORT), daemon=True)
    tcp_thread.start()

    # Start TUIO client thread
    tuio_thread = threading.Thread(target=start_tuio_client, args=(TUIO_ADDR,), daemon=True)
    tuio_thread.start()

    logger.info("✓ Broadcaster running. Waiting for connections...")
    logger.info("  Connect your C# client to tcp://127.0.0.1:8765")
    logger.info("  Or test with: nc localhost 8765")

    try:
        while running:
            time.sleep(0.5)
    except KeyboardInterrupt:
        shutdown()


if __name__ == "__main__":
    main()