# 🗺️ AR Indoor & Outdoor Navigation System

An advanced real-time navigation application built in Unity that overlays directional paths onto the physical world, designed to provide seamless guidance in both outdoor and complex indoor environments where GPS is unreliable.

---

## Problem Statement
Standard navigation apps like Google Maps are excellent for roads but fail in two key areas: **indoor spaces** (like malls, airports, or university campuses) and **"last 50 meters"** outdoor navigation where GPS accuracy degrades. This leads to confusion and a poor user experience. This project tackles that challenge by using Augmented Reality to provide precise, visual, turn-by-turn directions.

## The Solution
This application uses a combination of the device's camera, motion sensors (IMU), and mapping data to create an intuitive AR navigation experience. By rendering 3D arrows directly in the user's camera view, it eliminates the need to constantly look down at a 2D map, providing a safer and more immersive way to navigate.

---

## 📷 Live Demo

[INSERT A HIGH-QUALITY GIF OR YOUTUBE VIDEO OF YOUR APP IN ACTION HERE]
*A visual demonstration is critical for an AR project. A 15-30 second screen recording showing the arrows guiding a user is perfect.*

---

## ✨ Key Features

- **Real-time AR Path Rendering:** Overlays animated 3D arrows directly onto the user's camera feed, providing intuitive, real-time directional guidance.
- **Sensor Fusion for High Accuracy:** Dramatically improves positioning accuracy in GPS-weak zones by programmatically fusing data from the device's IMU (Inertial Measurement Unit) with visual SLAM tracking from the camera.
- **Persistent Multi-User Route Sharing:** Leverages Google Cloud Anchors to allow one user to map and save a route, which can then be loaded by other users in the same physical space for a shared experience.
- **Indoor & Outdoor Support:** Uses the Mapbox SDK for robust outdoor map data and routing, while relying on SLAM for precise indoor tracking where no GPS is available.

---

## ⚙️ How It Works

1.  **Route Calculation:** The user inputs a destination. The Mapbox SDK calculates the most efficient 2D route and provides a series of waypoint coordinates.
2.  **Coordinate Translation:** A custom C# script translates the 2D GPS waypoints from Mapbox into 3D coordinates relative to the user's real-world starting position.
3.  **World Tracking:** AR Foundation (using ARCore/ARKit) starts its SLAM algorithm, building a 3D map of the environment and tracking the device's position within it.
4.  **AR Rendering:** The application renders the 3D directional arrows at the calculated world-space coordinates, updating their visibility and orientation as the user moves along the path.

---

## 🛠️ Tech Stack Deep Dive

- **Engine & Language:** **Unity 3D** and **C#** (for real-time rendering and gameplay scripting).
- **Augmented Reality:** **AR Foundation** (as a high-level API for ARCore/ARKit), leveraging its **SLAM** capabilities for world-tracking.
- **Mapping & Routing:** **Mapbox SDK** (for generating outdoor map data and navigation routes).
- **Persistence & Sharing:** **Google Cloud Anchors** (for the multi-user route sharing feature).

---

## 🧠 Challenges & Future Work

- **Challenge:** One of the main challenges was the precise alignment of GPS coordinates from Mapbox with the AR world-space origin provided by AR Foundation, requiring custom calibration logic.
- **Future Work:**
    - Implement voice-guided turn-by-turn directions.
    - Develop a more advanced Point-of-Interest (POI) system with interactive information panels.
    - Optimize battery consumption during extended use.

---

## 🚀 Getting Started

### Prerequisites
- Unity Hub with Unity Editor 2022.3.x or later.
- An ARCore (Android) or ARKit (iOS) compatible device.
- A Mapbox API key.

### Installation
1. Clone the repository:
   ```sh
   git clone [https://github.com/technospes/AR-Navigation.git](https://github.com/technospes/AR-Navigation.git)
