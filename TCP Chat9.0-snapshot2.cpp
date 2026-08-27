#include <iostream>
#include <string>
#include <vector>
#include <thread>
#include <mutex>
#include <cstring>

#ifdef _WIN32
// Key: Define exclusion macros to avoid Windows GDI and USER conflicts with Raylib
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef NOGDI
#define NOGDI
#endif
#ifndef NOUSER
#define NOUSER
#endif

#include <winsock2.h>
#include <ws2tcpip.h>

// Undefine potentially remaining macros (safety)
#undef DrawText
#undef CloseWindow
#undef ShowCursor
#undef LoadImage
#undef DrawTextEx

// Use pragma comment only under MSVC, MinGW ignores it
#ifdef _MSC_VER
#pragma comment(lib, "ws2_32.lib")
#endif

typedef SOCKET SocketType;
#define CLOSE_SOCKET closesocket
#define SOCKET_ERROR_CODE SOCKET_ERROR
#else
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <unistd.h>
#include <fcntl.h>
#include <sys/select.h>
typedef int SocketType;
#define CLOSE_SOCKET close
#define SOCKET_ERROR_CODE -1
#define INVALID_SOCKET -1
#endif

#include <raylib.h>  // Must be included after Windows headers

constexpr int PORT = 5555;
constexpr int MAX_CLIENTS = 10;
constexpr int BUFFER_SIZE = 1024;
// Default window sizes (can be resized at runtime)
constexpr int DEFAULT_SCREEN_WIDTH = 800;
constexpr int DEFAULT_SCREEN_HEIGHT = 600;

std::mutex messagesMutex;
std::vector<std::string> chatMessages;

// Initialize network library (required on Windows)
bool InitNetwork() {
#ifdef _WIN32
	WSADATA wsaData;
	return WSAStartup(MAKEWORD(2, 2), &wsaData) == 0;
#else
	return true;
#endif
}

void CleanupNetwork() {
#ifdef _WIN32
	WSACleanup();
#endif
}

// Set socket to non-blocking mode
bool SetNonBlocking(SocketType sock) {
#ifdef _WIN32
	u_long mode = 1;
	return ioctlsocket(sock, FIONBIO, &mode) == 0;
#else
	int flags = fcntl(sock, F_GETFL, 0);
	if (flags == -1) return false;
	return fcntl(sock, F_SETFL, flags | O_NONBLOCK) != -1;
#endif
}

// Send string (handle partial sends)
bool SendAll(SocketType sock, const std::string& data) {
	int total = 0;
	int len = data.size();
	const char* buf = data.c_str();
	while (total < len) {
		int sent = send(sock, buf + total, len - total, 0);
		if (sent == SOCKET_ERROR_CODE) {
			return false;
		}
		total += sent;
	}
	return true;
}

// Add chat message (thread-safe)
void AddMessage(const std::string& msg) {
	std::lock_guard<std::mutex> lock(messagesMutex);
	chatMessages.push_back(msg);
	if (chatMessages.size() > 100) {
		chatMessages.erase(chatMessages.begin());
	}
}

// Helper function to draw centered text inside a rectangle
void DrawTextCenteredInRect(const char* text, Rectangle rect, int fontSize, Color color) {
	int textWidth = MeasureText(text, fontSize);
	int textX = rect.x + (rect.width - textWidth) / 2;
	int textY = rect.y + (rect.height - fontSize) / 2;
	DrawText(text, textX, textY, fontSize, color);
}

// ==================== Server ====================
void RunServer() {
	SocketType listenSock = socket(AF_INET, SOCK_STREAM, 0);
	if (listenSock == INVALID_SOCKET) {
		std::cerr << "Failed to create socket" << std::endl;
		return;
	}
	
	int opt = 1;
	setsockopt(listenSock, SOL_SOCKET, SO_REUSEADDR, (const char*)&opt, sizeof(opt));
	
	sockaddr_in serverAddr;
	serverAddr.sin_family = AF_INET;
	serverAddr.sin_addr.s_addr = INADDR_ANY;
	serverAddr.sin_port = htons(PORT);
	
	if (bind(listenSock, (sockaddr*)&serverAddr, sizeof(serverAddr)) == SOCKET_ERROR_CODE) {
		std::cerr << "Bind failed" << std::endl;
		CLOSE_SOCKET(listenSock);
		return;
	}
	
	if (listen(listenSock, MAX_CLIENTS) == SOCKET_ERROR_CODE) {
		std::cerr << "Listen failed" << std::endl;
		CLOSE_SOCKET(listenSock);
		return;
	}
	
	SetNonBlocking(listenSock);
	std::cout << "Server listening on port " << PORT << std::endl;
	AddMessage("Server started on port " + std::to_string(PORT));
	
	std::vector<SocketType> clientSocks;
	std::string inputBuffer;
	bool running = true;
	
	SetConfigFlags(FLAG_WINDOW_RESIZABLE);
	InitWindow(DEFAULT_SCREEN_WIDTH, DEFAULT_SCREEN_HEIGHT, "Chat Server");
	SetTargetFPS(60);
	
	while (running && !WindowShouldClose()) {
		int screenWidth = GetScreenWidth();
		int screenHeight = GetScreenHeight();
		
		// Accept new connections
		sockaddr_in clientAddr;
		socklen_t clientLen = sizeof(clientAddr);
		SocketType newClient = accept(listenSock, (sockaddr*)&clientAddr, &clientLen);
		if (newClient != INVALID_SOCKET) {
			SetNonBlocking(newClient);
			clientSocks.push_back(newClient);
			char ip[INET_ADDRSTRLEN];
			inet_ntop(AF_INET, &clientAddr.sin_addr, ip, INET_ADDRSTRLEN);
			std::string welcomeMsg = "Client connected: " + std::string(ip);
			AddMessage(welcomeMsg);
			for (SocketType sock : clientSocks) {
				if (sock != newClient) {
					SendAll(sock, welcomeMsg + "\n");
				}
			}
		}
		
		// Receive data from clients
		for (size_t i = 0; i < clientSocks.size(); ) {
			SocketType sock = clientSocks[i];
			char buffer[BUFFER_SIZE];
			int bytesReceived = recv(sock, buffer, BUFFER_SIZE - 1, 0);
			if (bytesReceived > 0) {
				buffer[bytesReceived] = '\0';
				std::string msg(buffer);
				if (!msg.empty() && msg.back() == '\n') msg.pop_back();
				if (!msg.empty()) {
					AddMessage(msg);
					for (SocketType client : clientSocks) {
						SendAll(client, msg + "\n");
					}
				}
				++i;
			} else if (bytesReceived == 0) {
				CLOSE_SOCKET(sock);
				clientSocks.erase(clientSocks.begin() + i);
				AddMessage("Client disconnected");
			} else {
				++i; // Non-blocking no data or temporary error, ignore
			}
		}
		
		// Handle server input
		int key = GetCharPressed();
		while (key > 0) {
			if (key >= 32 && key <= 125) {
				inputBuffer += (char)key;
			}
			key = GetCharPressed();
		}
		if (IsKeyPressed(KEY_BACKSPACE) && !inputBuffer.empty()) {
			inputBuffer.pop_back();
		}
		if (IsKeyPressed(KEY_ENTER) && !inputBuffer.empty()) {
			std::string msg = "Server: " + inputBuffer;
			AddMessage(msg);
			for (SocketType sock : clientSocks) {
				SendAll(sock, msg + "\n");
			}
			inputBuffer.clear();
		}
		
		// Draw
		BeginDrawing();
		ClearBackground(RAYWHITE);
		DrawText("Chat Server - Messages:", 10, 10, 20, DARKGRAY);
		
		// Display messages dynamically based on window height
		int y = 40;
		int maxMessagesY = screenHeight - 60; // Leave space for input area
		{
			std::lock_guard<std::mutex> lock(messagesMutex);
			for (const auto& msg : chatMessages) {
				if (y > maxMessagesY) break;
				DrawText(msg.c_str(), 10, y, 20, BLACK);
				y += 25;
			}
		}
		
		// Input area at bottom
		DrawText("Type message and press Enter:", 10, screenHeight - 40, 20, DARKGRAY);
		DrawRectangle(10, screenHeight - 20, screenWidth - 20, 1, LIGHTGRAY);
		DrawText(inputBuffer.c_str(), 10, screenHeight - 15, 20, BLUE);
		EndDrawing();
	}
	
	CloseWindow();
	for (SocketType sock : clientSocks) CLOSE_SOCKET(sock);
	CLOSE_SOCKET(listenSock);
}

// ==================== Client ====================
void RunClient(const std::string& serverIP) {
	SocketType clientSock = socket(AF_INET, SOCK_STREAM, 0);
	if (clientSock == INVALID_SOCKET) {
		std::cerr << "Failed to create socket" << std::endl;
		return;
	}
	
	sockaddr_in serverAddr;
	serverAddr.sin_family = AF_INET;
	serverAddr.sin_port = htons(PORT);
	inet_pton(AF_INET, serverIP.c_str(), &serverAddr.sin_addr);
	
	if (connect(clientSock, (sockaddr*)&serverAddr, sizeof(serverAddr)) == SOCKET_ERROR_CODE) {
		std::cerr << "Connection failed" << std::endl;
		CLOSE_SOCKET(clientSock);
		return;
	}
	
	SetNonBlocking(clientSock);
	std::cout << "Connected to server " << serverIP << std::endl;
	AddMessage("Connected to server");
	
	std::string inputBuffer;
	bool running = true;
	
	SetConfigFlags(FLAG_WINDOW_RESIZABLE);
	InitWindow(DEFAULT_SCREEN_WIDTH, DEFAULT_SCREEN_HEIGHT, "Chat Client");
	SetTargetFPS(60);
	
	while (running && !WindowShouldClose()) {
		int screenWidth = GetScreenWidth();
		int screenHeight = GetScreenHeight();
		
		char buffer[BUFFER_SIZE];
		int bytesReceived = recv(clientSock, buffer, BUFFER_SIZE - 1, 0);
		if (bytesReceived > 0) {
			buffer[bytesReceived] = '\0';
			AddMessage(buffer);
		} else if (bytesReceived == 0) {
			AddMessage("Server disconnected");
			running = false;
		}
		
		int key = GetCharPressed();
		while (key > 0) {
			if (key >= 32 && key <= 125) {
				inputBuffer += (char)key;
			}
			key = GetCharPressed();
		}
		if (IsKeyPressed(KEY_BACKSPACE) && !inputBuffer.empty()) {
			inputBuffer.pop_back();
		}
		if (IsKeyPressed(KEY_ENTER) && !inputBuffer.empty()) {
			SendAll(clientSock, inputBuffer + "\n");
			AddMessage("You: " + inputBuffer);
			inputBuffer.clear();
		}
		
		BeginDrawing();
		ClearBackground(RAYWHITE);
		DrawText("Chat Client - Messages:", 10, 10, 20, DARKGRAY);
		
		int y = 40;
		int maxMessagesY = screenHeight - 60;
		{
			std::lock_guard<std::mutex> lock(messagesMutex);
			for (const auto& msg : chatMessages) {
				if (y > maxMessagesY) break;
				DrawText(msg.c_str(), 10, y, 20, BLACK);
				y += 25;
			}
		}
		
		DrawText("Type message and press Enter:", 10, screenHeight - 40, 20, DARKGRAY);
		DrawRectangle(10, screenHeight - 20, screenWidth - 20, 1, LIGHTGRAY);
		DrawText(inputBuffer.c_str(), 10, screenHeight - 15, 20, BLUE);
		EndDrawing();
	}
	
	CloseWindow();
	CLOSE_SOCKET(clientSock);
}

// ==================== Main function: mode selection ====================
int main() {
	if (!InitNetwork()) {
		std::cerr << "Network initialization failed" << std::endl;
		return 1;
	}
	
	SetConfigFlags(FLAG_WINDOW_RESIZABLE);
	InitWindow(400, 250, "Select Mode");
	SetTargetFPS(60);
	
	std::string ipInput = "127.0.0.1";
	bool serverSelected = false;
	bool clientSelected = false;
	
	while (!WindowShouldClose()) {
		int screenWidth = GetScreenWidth();
		
		// 处理文本输入（IP 地址）
		int key = GetCharPressed();
		while (key > 0) {
			if (key >= 32 && key <= 125) {
				ipInput += (char)key;
			}
			key = GetCharPressed();
		}
		if (IsKeyPressed(KEY_BACKSPACE) && !ipInput.empty()) {
			ipInput.pop_back();
		}
		
		// 检测按钮点击
		Vector2 mousePos = GetMousePosition();
		bool mouseLeftPressed = IsMouseButtonPressed(MOUSE_LEFT_BUTTON);
		
		// 动态布局（避免重叠）
		float margin = 20;
		float titleY = margin;
		float labelY = titleY + 40;          // 标题下方 40 像素
		float inputBoxY = labelY + 25;       // 标签下方 25 像素
		float buttonY = inputBoxY + 50;      // 输入框下方 50 像素
		
		Rectangle inputBox = { margin, inputBoxY, screenWidth - 2 * margin, 30 };
		float buttonWidth = 160;
		float buttonHeight = 40;
		float gap = 20;
		float totalButtonsWidth = 2 * buttonWidth + gap;
		float startX = (screenWidth - totalButtonsWidth) / 2;
		
		Rectangle serverBtn = { startX, buttonY, buttonWidth, buttonHeight };
		Rectangle clientBtn = { startX + buttonWidth + gap, buttonY, buttonWidth, buttonHeight };
		
		if (mouseLeftPressed && CheckCollisionPointRec(mousePos, serverBtn)) {
			serverSelected = true;
			break;
		}
		if (mouseLeftPressed && CheckCollisionPointRec(mousePos, clientBtn)) {
			clientSelected = true;
			break;
		}
		
		// 绘制选择界面
		BeginDrawing();
		ClearBackground(RAYWHITE);
		
		const char* title = "Select Mode";
		int titleWidth = MeasureText(title, 20);
		DrawText(title, (screenWidth - titleWidth) / 2, titleY, 20, DARKGRAY);
		
		DrawText("Server IP (client mode):", margin, labelY, 20, DARKGRAY);
		DrawRectangleRec(inputBox, LIGHTGRAY);
		DrawRectangleLines(inputBox.x, inputBox.y, inputBox.width, inputBox.height, GRAY);
		DrawText(ipInput.c_str(), inputBox.x + 5, inputBox.y + 8, 20, BLACK);
		
		DrawRectangleRec(serverBtn, SKYBLUE);
		DrawTextCenteredInRect("Start Server", serverBtn, 20, BLACK);
		DrawRectangleRec(clientBtn, GREEN);
		DrawTextCenteredInRect("Start Client", clientBtn, 20, BLACK);
		
		EndDrawing();
	}
	
	CloseWindow(); // Close selection window
	
	if (serverSelected) {
		// Clear chat history
		{
			std::lock_guard<std::mutex> lock(messagesMutex);
			chatMessages.clear();
		}
		RunServer();
	} else if (clientSelected) {
		{
			std::lock_guard<std::mutex> lock(messagesMutex);
			chatMessages.clear();
		}
		RunClient(ipInput);
	}
	
	CleanupNetwork();
	return 0;
}
