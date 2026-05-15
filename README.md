# QuizChat Backend API

Đây là backend cho hệ thống QuizChat, được xây dựng bằng ASP.NET Core Web API. Hệ thống cung cấp các chức năng quản lý người dùng, ngân hàng câu hỏi, đề thi, chấm điểm tự động và tích hợp AI Chatbot (Google Gemini) để hỗ trợ tạo đề thi tự động.

## Hướng dẫn cài đặt & Chạy dự án

1. **Cấu hình Database & JWT:**
   - Mở file `appsettings.json` hoặc tạo file `.env`.
   - Đảm bảo bạn đã thay đổi chuỗi kết nối (`DefaultConnection`) cho đúng với SQL Server của bạn.
   - Cấu hình khóa bí mật `SecretKey` cho JWT và `ApiKey` cho Gemini AI.

2. **Khởi chạy ứng dụng:**
   - Mở Terminal/Command Prompt tại thư mục dự án.
   - Chạy lệnh: `dotnet run`
   - Truy cập vào giao diện Swagger tại (ví dụ): `http://localhost:5288/swagger` (cổng thực tế có thể hiển thị trên terminal).

---

## Hướng dẫn kiểm thử qua Swagger UI

Khi ứng dụng chạy, Swagger UI là công cụ trực quan nhất để bạn tương tác với các API. Hầu hết các API đều yêu cầu quyền truy cập (biểu tượng ổ khóa). Dưới đây là luồng thao tác chuẩn:

### 1. Xác thực (Đăng nhập)
Để lấy quyền sử dụng các API có ổ khóa:
1. Tìm API `POST /api/Auth/login`.
2. Truyền `username` và `password` vào Request Body.
3. Bấm **Execute**. Copy chuỗi `token` trong kết quả trả về.
4. Kéo lên đầu trang Swagger, bấm nút **Authorize** (ổ khóa).
5. Nhập vào ô trống theo cú pháp: `Bearer <chuỗi_token_của_bạn>`.
6. Bấm **Authorize**. Bây giờ bạn đã có quyền truy cập.

---

## Danh sách API (Endpoints)

Dưới đây là các module chính và URL để kiểm thử trên hệ thống.

### 1. Xác thực (Auth)
- **`POST /api/Auth/login`**: Đăng nhập và nhận JWT Token.

### 2. Ngân hàng câu hỏi (Questions)
- **`GET /api/Questions`**: Lấy danh sách tất cả câu hỏi (Yêu cầu đăng nhập).
- **`GET /api/Questions/random`**: Lấy danh sách câu hỏi ngẫu nhiên theo môn học và độ khó.
- **`POST /api/Questions`**: Thêm mới một câu hỏi (Chỉ Admin).
- **`PUT /api/Questions/{id}`**: Cập nhật câu hỏi (Chỉ Admin).
- **`DELETE /api/Questions/{id}`**: Xóa câu hỏi (Chỉ Admin).

### 3. Đề thi (Exams)
- **`GET /api/Exams`**: Xem danh sách đề thi (User chỉ thấy đề đã "Published", Admin thấy tất cả).
- **`GET /api/Exams/{id}`**: Xem thông tin cơ bản của một đề thi.
- **`GET /api/Exams/{id}/full`**: Xem toàn bộ đề thi kèm theo danh sách câu hỏi (Không yêu cầu đăng nhập - dùng để test nhanh).
- **`POST /api/Exams`**: Tạo một bộ khung đề thi mới (Chỉ Admin).
- **`PUT /api/Exams/{id}`**: Cập nhật thông tin đề thi (Chỉ Admin).
- **`DELETE /api/Exams/{id}`**: Xóa đề thi và các liên kết (Chỉ Admin).

### 4. Làm bài & Chấm điểm (ExamResults)
Đây là luồng API dành cho học sinh làm bài thi:
1. **`POST /api/ExamResults/start`**: Bắt đầu thi. Truyền `examId` vào body. Hệ thống sẽ trả về `resultId` (ID của lượt thi này).
2. **`POST /api/ExamResults/{resultId}/submit`**: Nộp bài. Truyền danh sách đáp án (`questionId`, `selectedOption`). Hệ thống tự động chấm điểm và trả về kết quả.
3. **`GET /api/ExamResults/{resultId}`**: Xem lại bài làm, hiển thị từng câu trả lời đúng/sai.
4. **`GET /api/ExamResults/my-history`**: Xem lịch sử tất cả các bài đã thi của User hiện tại.

### 5. Chatbot (AI Hỗ trợ)
- **`GET /api/Chatbot/list-models`**: Xem danh sách các model Gemini hiện có.
- **`POST /api/Chatbot/chat`**: Chat với AI. User có thể hỏi đáp bình thường. Đặc biệt, nếu Admin nhập các câu lệnh như *"Tạo đề thi C# 20 câu"*, hệ thống sẽ phân tích và tự động bốc câu hỏi từ DB sinh ra một đề thi mới.

---

**Phân quyền (RBAC):**
- Tài khoản thông thường (User): Được xem đề thi, làm bài thi, xem lịch sử và chat với AI.
- Tài khoản Quản trị (Admin): Ngoài quyền của User, có thêm quyền tạo/sửa/xóa câu hỏi và đề thi.
