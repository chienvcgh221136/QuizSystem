# QuizChat Backend API (AI-Powered Architecture)

Hệ thống Backend QuizChat là một nền tảng Web API mạnh mẽ được xây dựng trên nền tảng .NET hiện đại, tập trung vào việc tối ưu hóa quy trình quản lý giáo dục thông qua trí tuệ nhân tạo.

## Tổng quan kiến trúc

Backend được thiết kế theo kiến trúc **RESTful API** sử dụng **ASP.NET Core**, đảm bảo tính mở rộng cao và khả năng tích hợp linh hoạt với nhiều nền tảng frontend khác nhau.

### 1. Công nghệ cốt lõi (Core Stack)
*   **Framework**: .NET 8.0 (C#) - Cung cấp hiệu suất xử lý cao và tính bảo mật ổn định.
*   **Database**: Microsoft SQL Server - Hệ quản trị cơ sở dữ liệu quan hệ cho phép lưu trữ dữ liệu người dùng và ngân hàng đề thi một cách an toàn.
*   **ORM**: Entity Framework Core - Giúp quản lý truy vấn dữ liệu thông qua mã nguồn C#, tối ưu hóa hiệu năng làm việc với Database.
*   **Authentication**: JWT (JSON Web Token) - Cơ chế bảo mật không trạng thái (stateless), cho phép xác thực và phân quyền người dùng một cách hiệu quả.

### 2. Tích hợp Trí tuệ nhân tạo (AI Integration)
Hệ thống sử dụng **Groq Cloud AI** với mô hình **Llama-3.3-70b-versatile**. Đây là thành phần cốt lõi hỗ trợ Admin trong việc:
*   Phân tích cấu trúc câu hỏi từ ngôn ngữ tự nhiên.
*   Tự động sinh đề thi dựa trên thống kê thực tế từ ngân hàng câu hỏi.
*   Duy trì ngữ cảnh hội thoại thông qua cơ chế **Conversational Memory** (Lưu trữ lịch sử chat vào SQL Server).

### 3. Quy trình Xử lý Dữ liệu (Data Flow)
*   **Atomic Persistence**: Hệ thống sử dụng Database Transactions để đảm bảo các thao tác lưu đề thi trọn gói (Metadata + Questions) luôn diễn ra đồng bộ, tránh tình trạng dữ liệu rác.
*   **Auto-Migration**: Cơ chế tự động khởi tạo cấu trúc bảng (như `ChatMessages`) ngay khi ứng dụng khởi động, giúp rút ngắn thời gian triển khai (deployment).

---

## Tính năng chính

1. **Quản lý Ngân hàng Câu hỏi**: Lưu trữ, phân loại và tìm kiếm câu hỏi theo chủ đề và cấp độ.
2. **Trợ lý AI Soạn đề**: Hỗ trợ Admin tạo đề thi nhanh chóng thông qua giao tiếp ngôn ngữ tự nhiên.
3. **Quản lý Đề thi & Draft**: Luồng công việc từ Soạn thảo -> Lưu nháp -> Phê duyệt -> Xuất bản.
4. **Hệ thống Thi & Chấm điểm**: Tự động hóa việc bắt đầu bài thi, tính toán điểm số và lưu trữ lịch sử thi của học viên.
5. **Quản trị Người dùng**: Phân quyền Role-Based Access Control (RBAC) chặt chẽ giữa Admin và User.

---

## Hướng dẫn cài đặt & Chạy dự án

1. **Cấu hình Biến môi trường:**
   - Mở file `.env` tại thư mục `/backend`.
   - Cập nhật các thông tin quan trọng:
     ```env
     GROQ_API_KEY=gsk_your_key_here
     GROQ_MODEL=llama-3.3-70b-versatile
     CONNECTION_STRING=Server=your_server;Database=QuizApi;...
     ```

2. **Cơ sở dữ liệu:**
   - Hệ thống tự động kiểm tra và tạo các bảng cần thiết khi khởi động.

3. **Khởi chạy ứng dụng:**
   - Chạy lệnh: `dotnet run`
   - Giao diện Swagger (Tài liệu API): `http://localhost:5288/swagger`

---

## Danh sách API chi tiết (Full API Reference)

Hệ thống cung cấp đầy đủ các phương thức tương tác thông qua 7 nhóm Controller chính:

### 1. Xác thực (Auth)
*   **POST `/api/Auth/login`**: Đăng nhập và nhận JWT Token + thông tin cơ bản của User.

### 2. Quản trị hệ thống (Admin)
*   **GET `/api/Admin/stats`**: Lấy thống kê số lượng User, Đề thi, Câu hỏi và Điểm trung bình toàn hệ thống.
*   **GET `/api/Admin/recent-activity`**: Lấy danh sách 10 lượt làm bài gần nhất của học viên.

### 3. Quản lý Người dùng (Users) - Yêu cầu quyền Admin
*   **GET `/api/Users`**: Lấy danh sách toàn bộ người dùng trong hệ thống.
*   **GET `/api/Users/{id}`**: Lấy thông tin chi tiết một người dùng theo ID.
*   **POST `/api/Users`**: Tạo mới một tài khoản người dùng (Admin có quyền tạo bất kỳ Role nào).
*   **PUT `/api/Users/{id}`**: Cập nhật thông tin (Họ tên, Email, Role, Mật khẩu) của người dùng.
*   **DELETE `/api/Users/{id}`**: Xóa vĩnh viễn một tài khoản người dùng.
*   **GET `/api/Users/{id}/history`**: Xem lịch sử thi chi tiết của một người dùng (Tên đề, Điểm số, Thời gian nộp bài).

### 4. Quản lý Câu hỏi (Questions)
*   **GET `/api/Questions`**: Lấy toàn bộ danh sách câu hỏi trong ngân hàng.
*   **GET `/api/Questions/{id}`**: Lấy nội dung chi tiết một câu hỏi.
*   **GET `/api/Questions/categories`**: Lấy danh sách tất cả các chủ đề (Category) hiện có.
*   **GET `/api/Questions/random`**: Bốc ngẫu nhiên câu hỏi theo Category, Level và Số lượng yêu cầu.
*   **POST `/api/Questions`**: (Admin) Thêm câu hỏi mới (Nội dung, 4 đáp án, Đáp án đúng, Giải thích).
*   **PUT `/api/Questions/{id}`**: (Admin) Cập nhật thông tin câu hỏi hiện có.
*   **DELETE `/api/Questions/{id}`**: (Admin) Xóa câu hỏi khỏi ngân hàng.

### 5. Quản lý Đề thi (Exams)
*   **GET `/api/Exams`**: Lấy danh sách đề thi (User chỉ thấy trạng thái Published).
*   **GET `/api/Exams/{id}`**: Lấy thông tin mô tả cơ bản của đề thi.
*   **GET `/api/Exams/{id}/full`**: Lấy toàn bộ đề thi bao gồm cả danh sách câu hỏi (Dùng cho giao diện làm bài).
*   **POST `/api/Exams`**: (Admin) Tạo một bộ khung đề thi mới.
*   **POST `/api/Exams/full`**: (Admin) API đặc biệt: Lưu đồng thời thông tin Đề thi + Danh sách câu hỏi mới từ AI.
*   **PUT `/api/Exams/{id}`**: (Admin) Cập nhật thông tin đề thi.
*   **DELETE `/api/Exams/{id}`**: (Admin) Xóa đề thi và các liên kết liên quan.

### 6. Làm bài & Kết quả (ExamResults)
*   **POST `/api/ExamResults/start`**: Khởi tạo phiên thi (Ghi nhận thời gian bắt đầu).
*   **POST `/api/ExamResults/{resultId}/submit`**: Nộp bài, chấm điểm và lưu kết quả cuối cùng.
*   **GET `/api/ExamResults/{id}`**: Xem chi tiết kết quả một bài thi (Đúng/Sai từng câu).
*   **GET `/api/ExamResults/my-history`**: Xem danh sách lịch sử thi của cá nhân người dùng đang đăng nhập.

### 7. Trợ lý AI (Chatbot)
*   **POST `/api/Chatbot/chat`**: Gửi yêu cầu cho AI Tutor (Hỗ trợ ngữ cảnh lịch sử chat).
*   **GET `/api/Chatbot/list-models`**: Xem danh sách các dòng AI đang hoạt động.

---

## Chức năng Chi tiết (User vs Admin)

### Phân hệ Quản trị (Admin Dashboard)
*   **AI Tutor Assistant**: Soạn đề thông minh, ghi nhớ ngữ cảnh hội thoại.
*   **Drafting Workflow**: Luồng làm việc chuyên nghiệp (Soạn thảo -> Xem nháp -> Lưu/Xuất bản).
*   **Quản lý Ngân hàng**: Tìm kiếm thông minh câu hỏi ngay trong Modal.

### Phân hệ Người dùng (User Portal)
*   **Exam Center**: Xem và tham gia các đề thi đã xuất bản.
*   **Chấm điểm tự động**: Biết điểm ngay sau khi nộp bài và xem lại giải thích chi tiết.
*   **Hỗ trợ AI**: Trò chuyện với trợ lý để giải đáp kiến thức học tập.

---
*Phát triển bởi Đội ngũ QuizChat - Tích hợp Groq Cloud AI 2024.*
