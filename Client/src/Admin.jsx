import { useState, useEffect } from 'react';
import axios from 'axios';
import { Lock, Upload, FileSpreadsheet, Check, Download, RefreshCw, Calendar, FileText, Eye, Loader2, User, Search, Trash2, DollarSign, FileCheck } from 'lucide-react';

export default function Admin() {
  const [isLoggedIn, setIsLoggedIn] = useState(false);
  const [password, setPassword] = useState('');
  const [uploading, setUploading] = useState(false);
  const [msg, setMsg] = useState('');
  const [currentFile, setCurrentFile] = useState(null);
  const [searchStudentId, setSearchStudentId] = useState('');
  const [studentDebts, setStudentDebts] = useState(null);
  const [loadingDebts, setLoadingDebts] = useState(false);
  const [exemptLoading, setExemptLoading] = useState(null);

  const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || "http://localhost:5219";

  // פונקציה לטעינת מידע על הקובץ הקיים בשרת
  const fetchFileInfo = async () => {
    try {
      const res = await axios.get(`${API_BASE_URL}/api/admin/current-file-info`);
      if (res.data.exists) {
        setCurrentFile(res.data);
      } else {
        setCurrentFile(null);
      }
    } catch (e) { 
      console.error("Error fetching file info:", e); 
    }
  };

  // טעינת המידע מיד לאחר כניסה
  useEffect(() => {
    if (isLoggedIn) {
        fetchFileInfo();
    }
  }, [isLoggedIn]);

  const handleLogin = async () => {
    try {
      await axios.post(`${API_BASE_URL}/api/admin/login`, { password });
      setIsLoggedIn(true);
    } catch (e) {
      alert('סיסמה שגויה');
    }
  };

  const handleFileUpload = async (e) => {
    const file = e.target.files[0];
    if (!file) return;
    
    if (!confirm("שימי לב: פעולה זו תבצע סנכרון מלא.\nחובות שאינם מופיעים בקובץ החדש יוסרו מהתצוגה בבוט.\nהאם להמשיך?")) return;

    setUploading(true);
    setMsg('');
    
    const formData = new FormData();
    formData.append('file', file);

    try {
      const res = await axios.post(`${API_BASE_URL}/api/admin/upload-excel`, formData);
      setMsg(res.data.message);
      // רענון המידע על הקובץ מיד אחרי ההעלאה
      await fetchFileInfo(); 
    } catch (error) {
      alert('שגיאה בהעלאה: ' + (error.response?.data || error.message));
    } finally {
      setUploading(false);
    }
  };

  // פונקציה להורדת הקובץ הקיים
  const downloadCurrent = () => {
      // פתיחת הקישור בחלון חדש מורידה את הקובץ
      window.open(`${API_BASE_URL}/api/admin/download-current`, '_blank');
  };

  const handleDownloadReport = async () => {
    try {
      const response = await axios.get(`${API_BASE_URL}/api/admin/export-submissions`, {
        responseType: 'blob',
      });
      const url = window.URL.createObjectURL(new Blob([response.data]));
      const link = document.createElement('a');
      link.href = url;
      link.setAttribute('download', 'SubmissionsReport.xlsx');
      document.body.appendChild(link);
      link.click();
      link.remove();
    } catch (error) { alert('שגיאה בהורדת הדוח'); }
  };

  // ===== פונקציות ניהול חריגים =====
  
  const handleSearchStudent = async () => {
    if (!searchStudentId.trim()) {
      alert('אנא הכנס ת.ז של תלמידה');
      return;
    }

    setLoadingDebts(true);
    try {
      const res = await axios.get(`${API_BASE_URL}/api/admin/student-debts/${searchStudentId}`);
      setStudentDebts(res.data);
    } catch (error) {
      alert('תלמידה לא נמצאה או שגיאה בשרת');
      setStudentDebts(null);
    } finally {
      setLoadingDebts(false);
    }
  };

  const handleExempt = async (debtId, action) => {
    let confirmMsg = '';
    
    if (action === 'remove-debt') {
      confirmMsg = '⚠️ זה ימחק את הקורס לגמרי מהמסד נתונים!\nהאם אתה בטוח/בטוחה?';
    } else if (action === 'exempt-completely') {
      confirmMsg = 'זה יסמן את הקורס כפטור מלא (תשלום + הגשה).\nהקורס יישאר בבסיס הנתונים.\nהאם להמשיך?';
    } else {
      confirmMsg = 'האם אתה בטוח/בטוחה?';
    }
    
    if (!window.confirm(confirmMsg)) return;

    setExemptLoading(debtId);
    try {
      const url = `${API_BASE_URL}/api/admin/${action}/${debtId}`;
      await axios.post(url);
      
      // שדרוג קטן של הודעה
      const actionText = action === 'remove-debt' ? '🗑️ קורס נמחק לגמרי' : 
                         action === 'exempt-payment' ? '✓ פטור מתשלום' :
                         action === 'exempt-submission' ? '✓ פטור מהגשה' :
                         '✓ פטור מלא';
      
      setMsg(`${actionText}`);
      
      // שדרוג רשימה
      await handleSearchStudent();
    } catch (error) {
      alert('שגיאה בביצוע הפעולה: ' + error.response?.data?.message);
    } finally {
      setExemptLoading(null);
    }
  };

  // --- מסך התחברות ---
  if (!isLoggedIn) {
    return (
      <div className="flex h-screen items-center justify-center bg-gray-100" dir="rtl">
        <div className="bg-white p-8 rounded-xl shadow-lg w-96 text-center border border-gray-200">
          <div className="bg-blue-100 p-4 rounded-full w-fit mx-auto mb-4 text-blue-600">
            <Lock size={32} />
          </div>
          <h2 className="text-2xl font-bold mb-4 text-gray-800">כניסת הנהלה</h2>
          <input 
            type="password" 
            placeholder="סיסמה" 
            className="w-full p-3 border rounded-lg mb-4 text-center focus:ring-2 focus:ring-blue-500 outline-none"
            value={password}
            onChange={e => setPassword(e.target.value)}
          />
          <button onClick={handleLogin} className="w-full bg-blue-600 text-white p-3 rounded-lg hover:bg-blue-700 font-bold shadow transition">
            כניסה למערכת
          </button>
        </div>
      </div>
    );
  }

  // --- מסך ניהול ---
  return (
    <div className="p-8 bg-gray-50 min-h-screen font-sans" dir="rtl">
      <div className="max-w-4xl mx-auto">
        <div className="flex justify-between items-center mb-8 pb-4 border-b border-gray-200">
            <h1 className="text-3xl font-black text-gray-800 flex items-center gap-3">
                <Lock className="text-blue-600"/> ממשק ניהול - בוט השלמות
            </h1>
            <button 
                onClick={() => setIsLoggedIn(false)} 
                className="text-red-500 hover:bg-red-50 px-4 py-2 rounded-lg font-bold text-sm transition"
            >
                יציאה
            </button>
        </div>
        
        <div className="grid gap-6">
            {/* 1. כרטיס קובץ נתונים נוכחי (התווסף!) */}
            <div className="bg-white p-6 rounded-2xl shadow-sm border border-blue-100">
                <h3 className="text-xl font-bold mb-4 text-gray-800 flex items-center gap-2">
                    <FileText className="text-blue-600"/> קובץ נתונים פעיל
                </h3>
                
                {currentFile ? (
                    <div className="flex flex-col sm:flex-row sm:items-center justify-between bg-blue-50 p-4 rounded-xl border border-blue-200 gap-4">
                        <div>
                            <div className="font-bold text-blue-900 text-lg flex items-center gap-2">
                                <Check className="text-green-600" size={20}/>
                                {currentFile.fileName}
                            </div>
                            <div className="text-sm text-blue-700 flex items-center gap-1 mt-1">
                                <Calendar size={14}/> תאריך העלאה: {new Date(currentFile.lastModified).toLocaleString()}
                            </div>
                        </div>
                        
                         {/* כפתור הצפייה  */}
                        <button 
                            onClick={downloadCurrent} 
                            className="bg-white text-blue-700 px-5 py-2.5 rounded-lg border border-blue-200 hover:bg-blue-600 hover:text-white font-bold shadow-sm flex items-center gap-2 transition"
                        >
                            <Eye size={18}/> צפייה / הורדת הקובץ
                        </button>
                    </div>
                ) : (
                    <div className="text-gray-500 italic p-6 bg-gray-50 rounded-xl text-center border border-dashed border-gray-300">
                        <div className="mb-2">לא נמצא קובץ נתונים בשרת.</div>
                        <div className="text-sm">יש להעלות קובץ חדש כדי שיופיע כאן.</div>
                    </div>
                )}
            </div>

            {/* 2. כרטיס העלאה וסנכרון */}
            <div className="bg-white p-6 rounded-2xl shadow-sm border border-gray-200">
                <h3 className="text-xl font-bold mb-2 text-gray-800 flex items-center gap-2">
                    <RefreshCw className="text-orange-500"/>
                    העלאת קובץ חדש (עדכון)
                </h3>
                <p className="text-gray-500 mb-6 text-sm">
                    העלאת קובץ תחליף את הקובץ הישן ותבצע <b>סנכרון מלא</b> של החובות במערכת.
                </p>

                <label className={`block w-full border-2 border-dashed ${uploading ? 'border-blue-300 bg-blue-50' : 'border-gray-300 hover:border-blue-400 hover:bg-gray-50'} rounded-xl p-10 text-center cursor-pointer transition duration-300 group`}>
                    <input type="file" className="hidden" accept=".xlsx" onChange={handleFileUpload} disabled={uploading} />
                    {uploading ? (
                        <div className="flex flex-col items-center gap-2">
                            <Loader2 className="animate-spin text-blue-600" size={40}/>
                            <span className="text-blue-600 font-bold">מעבד נתונים ומסנכרן... נא להמתין</span>
                        </div>
                    ) : (
                        <div className="flex flex-col items-center gap-3 text-gray-500 group-hover:text-blue-600">
                            <div className="bg-blue-100 p-4 rounded-full text-blue-600 group-hover:bg-blue-200 transition">
                                <Upload size={32} />
                            </div>
                            <div>
                                <span className="font-bold text-lg block text-gray-800">לחצי כאן לבחירת קובץ Excel לעדכון</span>
                                <span className="text-sm opacity-70">סיומת xlsx בלבד</span>
                            </div>
                        </div>
                    )}
                </label>

                {msg && (
                    <div className="mt-6 p-4 bg-green-50 text-green-800 rounded-xl flex items-center gap-3 border border-green-200 animate-in fade-in slide-in-from-bottom-2">
                        <div className="bg-green-200 p-1 rounded-full"><Check size={16}/></div>
                        {msg}
                    </div>
                )}
            </div>

            {/* 3. כרטיס דוחות */}
            <div className="bg-white p-6 rounded-2xl shadow-sm border border-gray-200">
                <h3 className="text-xl font-bold mb-4 flex items-center gap-2">
                    <FileSpreadsheet className="text-green-600"/>
                    דוחות וסטטיסטיקה
                </h3>
                <button 
                    onClick={handleDownloadReport}
                    className="w-full sm:w-auto bg-green-600 hover:bg-green-700 text-white px-6 py-3 rounded-xl flex items-center justify-center gap-2 font-bold shadow-md transition transform active:scale-95"
                >
                    <Download size={20}/>
                    הורדת דוח הגשות (Excel)
                </button>
            </div>

            {/* 4. כרטיס ניהול חריגים חדש! */}
            <div className="bg-white p-6 rounded-2xl shadow-sm border border-purple-200 bg-gradient-to-br from-purple-50 to-white">
                <h3 className="text-xl font-bold mb-4 flex items-center gap-2 text-purple-900">
                    <User className="text-purple-600"/>
                    ניהול חריגים - פטורים והסרות
                </h3>
                
                {/* טופס חיפוש */}
                <div className="flex flex-col sm:flex-row gap-3 mb-6">
                    <input 
                        type="text" 
                        placeholder="הכנס ת.ז של תלמידה..."
                        className="flex-1 px-4 py-3 border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-purple-500 text-right"
                        value={searchStudentId}
                        onChange={e => setSearchStudentId(e.target.value)}
                        onKeyPress={e => e.key === 'Enter' && handleSearchStudent()}
                        dir="rtl"
                    />
                    <button 
                        onClick={handleSearchStudent}
                        disabled={loadingDebts}
                        className="bg-purple-600 hover:bg-purple-700 disabled:bg-gray-400 text-white px-6 py-3 rounded-lg font-bold flex items-center justify-center gap-2 transition"
                    >
                        {loadingDebts ? <Loader2 className="animate-spin" size={20}/> : <Search size={20}/>}
                        חיפוש
                    </button>
                </div>

                {msg && (
                    <div className="mb-4 p-4 bg-green-50 text-green-800 rounded-xl flex items-center gap-3 border border-green-200 animate-in fade-in">
                        <Check size={20}/>
                        {msg}
                    </div>
                )}

                {/* תוצאות חיפוש */}
                {studentDebts && (
                    <div className="space-y-4">
                        <h4 className="font-bold text-gray-800 mb-4">חובות של {searchStudentId}</h4>
                        {studentDebts.length === 0 ? (
                            <div className="text-gray-500 italic text-center p-6 bg-gray-50 rounded-lg">
                                אין חובות להציג
                            </div>
                        ) : (
                            <div className="space-y-3 max-h-96 overflow-y-auto">
                                {studentDebts.map((debt) => (
                                    <div key={debt.debtID} className="p-4 border border-gray-200 rounded-lg hover:shadow-md transition">
                                        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 mb-3">
                                            <div>
                                                <p className="text-sm text-gray-600">שם קורס</p>
                                                <p className="font-bold text-gray-800">{debt.lessonName}</p>
                                            </div>
                                            <div>
                                                <p className="text-sm text-gray-600">סטטוס</p>
                                                <p className="font-bold">
                                                    {debt.isActive ? (
                                                        <span className="text-orange-600">פעיל</span>
                                                    ) : (
                                                        <span className="text-gray-500">אינו פעיל</span>
                                                    )}
                                                </p>
                                            </div>
                                            <div>
                                                <p className="text-sm text-gray-600">תשלום</p>
                                                <p className={debt.isPaid ? "text-green-600 font-bold" : "text-red-600 font-bold"}>
                                                    {debt.isPaid ? "✓ שולם" : "✗ חוב"}
                                                </p>
                                            </div>
                                            <div>
                                                <p className="text-sm text-gray-600">הגשה</p>
                                                <p className={debt.isSubmitted ? "text-green-600 font-bold" : "text-red-600 font-bold"}>
                                                    {debt.isSubmitted ? "✓ הוגש" : "✗ לא הוגש"}
                                                </p>
                                            </div>
                                        </div>

                                        <div className="flex flex-wrap gap-2 pt-3 border-t border-gray-100">
                                            {debt.isActive && (
                                                <>
                                                    {!debt.isPaid && (
                                                        <button 
                                                            onClick={() => handleExempt(debt.debtID, 'exempt-payment')}
                                                            disabled={exemptLoading === debt.debtID}
                                                            className="flex-1 min-w-[120px] bg-blue-500 hover:bg-blue-600 disabled:bg-gray-400 text-white px-3 py-2 rounded-lg text-sm font-bold flex items-center justify-center gap-2 transition"
                                                            title="יסמן את הקורס כ'שולם' אך לא יוציא את ההגשה"
                                                        >
                                                            {exemptLoading === debt.debtID ? <Loader2 className="animate-spin" size={14}/> : <DollarSign size={14}/>}
                                                            פטור מתשלום
                                                        </button>
                                                    )}
                                                    {!debt.isSubmitted && (
                                                        <button 
                                                            onClick={() => handleExempt(debt.debtID, 'exempt-submission')}
                                                            disabled={exemptLoading === debt.debtID}
                                                            className="flex-1 min-w-[120px] bg-cyan-500 hover:bg-cyan-600 disabled:bg-gray-400 text-white px-3 py-2 rounded-lg text-sm font-bold flex items-center justify-center gap-2 transition"
                                                            title="יסמן את הקורס כ'הוגש' אך לא יוציא את התשלום"
                                                        >
                                                            {exemptLoading === debt.debtID ? <Loader2 className="animate-spin" size={14}/> : <FileCheck size={14}/>}
                                                            פטור מהגשה
                                                        </button>
                                                    )}
                                                    {(!debt.isPaid || !debt.isSubmitted) && (
                                                        <button 
                                                            onClick={() => handleExempt(debt.debtID, 'exempt-completely')}
                                                            disabled={exemptLoading === debt.debtID}
                                                            className="flex-1 min-w-[120px] bg-amber-500 hover:bg-amber-600 disabled:bg-gray-400 text-white px-3 py-2 rounded-lg text-sm font-bold flex items-center justify-center gap-2 transition"
                                                            title="פטור מלא - הקורס יישאר בבסיס הנתונים אך יסומן כ'בוצע'"
                                                        >
                                                            {exemptLoading === debt.debtID ? <Loader2 className="animate-spin" size={14}/> : <Check size={14}/>}
                                                            פטור מלא
                                                        </button>
                                                    )}
                                                    <button 
                                                        onClick={() => handleExempt(debt.debtID, 'remove-debt')}
                                                        disabled={exemptLoading === debt.debtID}
                                                        className="flex-1 min-w-[120px] bg-red-500 hover:bg-red-600 disabled:bg-gray-400 text-white px-3 py-2 rounded-lg text-sm font-bold flex items-center justify-center gap-2 transition"
                                                        title="⚠️ מחיקה לגמרי - הקורס יוסר מהמסד נתונים"
                                                    >
                                                        {exemptLoading === debt.debtID ? <Loader2 className="animate-spin" size={14}/> : <Trash2 size={14}/>}
                                                        הסר קורס
                                                    </button>
                                                </>
                                            )}
                                        </div>
                                    </div>
                                ))}
                            </div>
                        )}
                    </div>
                )}
            </div>
        </div>
      </div>
    </div>
  );
}