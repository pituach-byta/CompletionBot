import { useState, useRef, useEffect } from 'react';
import axios from 'axios';
import { Send, Upload, CreditCard, FileText, CheckCircle, AlertCircle, Loader2, Lock, X, Check, Clock, Ban, Book, ExternalLink, Download } from 'lucide-react';
import { Link } from 'react-router-dom';

// --- פונקציות עזר ---
const getVal = (obj, key1, key2) => {
  if (!obj) return null;
  return obj[key1] !== undefined ? obj[key1] : obj[key2];
};

// --- רכיב 1: חלונית תשלום (Iframe) ---
const PaymentIframe = ({ totalAmount, student, onSuccess, onClose }) => {
  const iframeRef = useRef(null);
  const [status, setStatus] = useState('מאתחל מערכת תשלומים...');

  const initIframe = () => {
    if (iframeRef.current && iframeRef.current.contentWindow) {
      const postData = {
        Name: 'PostNedarim',
        Value: {
          Mosad: '7001475',
          ApiValid: 'MykxduB97f',
          Zeout: student.StudentID || '',
          FirstName: student.FirstName || '',
          LastName: student.LastName || '',
          ClientName: `${student.FirstName} ${student.LastName}`,
          PaymentType: 'Ragil',
          Amount: totalAmount,
          Tashlumim: '1',
          Comment: 'תשלום עבור השלמת עבודות',
          WaitFrame: '1',
          ForceUpdateMatching: '1',
          CallBack: '',
          Street: 'לא הוזן', City: 'לא הוזן', Phone: '0000000000', Mail: ''
        }
      };
      iframeRef.current.contentWindow.postMessage(postData, '*');
      setStatus('המערכת מוכנה. אנא הזיני פרטי אשראי ולחצי על אישור למטה.');
    }
  };

  const executePayment = () => {
    if (iframeRef.current && iframeRef.current.contentWindow) {
      const actionData = { Name: 'FinishTransaction' };
      iframeRef.current.contentWindow.postMessage(actionData, '*');
      setStatus('מבצע תשלום, נא להמתין...');
    }
  };

  const bypassPayment = () => {
    if (window.confirm("האם לדמות תשלום מוצלח? (מיועד לבדיקות פיתוח בלבד)")) {
      onSuccess("TEST-TRANSACTION-123");
    }
  };

  useEffect(() => {
    const handleMessage = (event) => {
      const data = event.data;
      if (!data || !data.Name) return;

      if (data.Name === 'Resize') {
        if (iframeRef.current) {
          iframeRef.current.style.height = (data.Value + 20) + 'px';
        }
        setTimeout(initIframe, 500);
      }

      if (data.Name === 'TransactionResponse') {
        if (data.Value.Status === 'OK') {
          onSuccess(data.Value.TransactionId);
        } else {
          const msg = data.Value.Message || 'שגיאה כללית';
          if (msg !== "מספר עסקה לא תקין") setStatus('התשלום נכשל: ' + msg);
        }
      }
    };

    window.addEventListener('message', handleMessage);
    const timeout = setTimeout(initIframe, 2000);
    return () => {
      window.removeEventListener('message', handleMessage);
      clearTimeout(timeout);
    };
  }, [onSuccess, totalAmount, student]);

  return (
    <div className="fixed inset-0 bg-black/60 backdrop-blur-sm flex items-center justify-center z-[9999] p-4" style={{ direction: 'rtl' }}>
      <div className="bg-white w-full max-w-lg rounded-2xl shadow-2xl overflow-hidden flex flex-col max-h-[90vh]">
        <div className="bg-gradient-to-l from-green-600 to-teal-600 p-4 flex justify-between items-center text-white">
          <div>
            <h3 className="font-bold text-lg flex items-center gap-2"><CreditCard className="h-5 w-5" /> תשלום אשראי</h3>
            <p className="text-sm opacity-90 mt-1">עבור: <b>{student.FirstName} {student.LastName}</b> | סכום: <b>{totalAmount} ₪</b></p>
          </div>
          <button onClick={onClose} className="p-2 hover:bg-white/20 rounded-full transition"><X size={20} /></button>
        </div>
        <div className="bg-yellow-50 text-yellow-800 text-xs p-2 text-center border-b border-yellow-100">{status}</div>
        <div className="flex-1 overflow-y-auto bg-white relative p-1">
          <iframe ref={iframeRef} src="https://www.matara.pro/nedarimplus/iframe/" className="w-full border-none" style={{ minHeight: '400px', width: '100%' }} title="Payment Frame" sandbox="allow-scripts allow-same-origin allow-forms allow-popups" />
        </div>
        <div className="p-4 border-t bg-gray-50 flex justify-center flex-col gap-2">
          <button onClick={executePayment} className="w-full bg-green-600 hover:bg-green-700 text-white font-bold py-3 px-6 rounded-xl shadow-lg transform active:scale-95 transition flex items-center justify-center gap-2"><Check size={20} /> אישור וביצוע תשלום</button>
          <button onClick={bypassPayment} className="w-full bg-gray-200 hover:bg-gray-300 text-gray-600 text-xs py-2 px-4 rounded-lg flex items-center justify-center gap-1"><AlertCircle size={14} /> עקוף תשלום (מצב פיתוח)</button>
        </div>
      </div>
    </div>
  );
};

// --- רכיב 2: רשימת החובות (מעודכן לוגית וויזואלית) ---
const DebtsList = ({ debts, onPay, onUpload, uploadingId }) => {
  const unpaidDebts = debts.filter(d => !getVal(d, 'isPaid', 'IsPaid'));
  const totalAmount = unpaidDebts.length * 1;

  let accumulatedHours = 0;
  const LIMIT = 300;
  const allowedSubmissionIds = new Set();

  const sortedDebts = [...debts].sort((a, b) => getVal(a, 'debtID', 'DebtID') - getVal(b, 'debtID', 'DebtID'));

  sortedDebts.forEach(d => {
    const hours = Number(getVal(d, 'hours', 'Hours') || 0);
    const isExempt = getVal(d, 'isExempt', 'IsExempt');
    const type = getVal(d, 'lessonType', 'LessonType') || '';

    // בדיקה האם הקורס הוא מסוג שחייב להיות פתוח תמיד
    const isMandatory = type.includes('חובה') || 
                        type.includes('מודרכת') || 
                        type.includes('מתוקשב') || 
                        type.includes('מוקשבים');

    if (isMandatory) {
        allowedSubmissionIds.add(getVal(d, 'debtID', 'DebtID'));
    } else {
        if (accumulatedHours < LIMIT && !isExempt) {
            allowedSubmissionIds.add(getVal(d, 'debtID', 'DebtID'));
            accumulatedHours += hours;
        }
    }
  });

  if (unpaidDebts.length > 0) {
    return (
      <div className="mt-4 bg-white p-4 rounded-xl shadow-sm border border-gray-100">
        <h4 className="font-bold text-gray-800 mb-3 border-b pb-2 flex items-center gap-2">
          <Lock size={18} className="text-red-500" />
          <span>הקורסים נעולים. <span className="text-green-600 font-bold">התשלום יפתח מיד</span> את האפשרות להגשה.</span>
        </h4>
        <ul className="space-y-2">
          {debts.map((debt, idx) => (
            <li key={idx} className="text-gray-600 text-sm flex items-center gap-2">
              <span className="w-1.5 h-1.5 bg-gray-400 rounded-full"></span>
              {getVal(debt, 'lessonName', 'LessonName')}
              <span className="text-gray-400 text-xs">({getVal(debt, 'lessonType', 'LessonType')})</span>
            </li>
          ))}
        </ul>
      </div>
    );
  }

  return (
    <div className="mt-4 space-y-4">
      {totalAmount === 0 && unpaidDebts.length === 0 && debts.length > 0 && (
        <div className="bg-green-50/50 border border-green-100 p-3 rounded-xl text-center text-green-700 font-bold text-sm">
          <CheckCircle className="inline-block ml-1" size={16} /> כל דמי הטיפול שולמו.
        </div>
      )}

      {debts.map((debt, idx) => {
        const isPaid = getVal(debt, 'isPaid', 'IsPaid');
        const isSubmitted = getVal(debt, 'isSubmitted', 'IsSubmitted');
        const link = getVal(debt, 'materialLink', 'MaterialLink');
        const debtId = getVal(debt, 'debtID', 'DebtID');
        const hours = getVal(debt, 'hours', 'Hours') || 0;
        const isExempt = getVal(debt, 'isExempt', 'IsExempt');
        const type = getVal(debt, 'lessonType', 'LessonType') || '';

        const canSubmit = allowedSubmissionIds.has(debtId) && !isExempt;
        
        // --- לוגיקת תצוגה ---
        const isClassroom = type.includes('חובה') || type.includes('מתוקשב') || type.includes('מודרכת');
        const isUrl = link && (link.trim().startsWith('http') || link.trim().startsWith('www'));

        return (
          <div key={idx} className={`p-5 rounded-xl shadow-sm border flex flex-col gap-3 transition-all hover:shadow-md ${canSubmit ? 'bg-white border-gray-100' : 'bg-gray-50 border-gray-200 opacity-80'}`}>
            <div className="flex justify-between items-start">
              <div className="flex-1">
                <h4 className="font-bold text-gray-800 text-lg flex items-center gap-2">
                  {getVal(debt, 'lessonName', 'LessonName')}
                  {!canSubmit && <span className="text-xs bg-gray-200 text-gray-600 px-2 py-0.5 rounded-full font-normal">פטורה</span>}
                </h4>
                <p className="text-sm text-gray-500 mt-0.5">
                  {type} | {getVal(debt, 'lecturerName', 'LecturerName')}
                  <span className="mr-2 font-medium text-blue-600/80">({hours} שעות)</span>
                </p>
              </div>
            </div>

            <div className="flex flex-wrap gap-4 items-center text-sm mt-1">
              <div className={`flex items-center gap-1.5 font-medium ${isPaid ? 'text-green-600' : 'text-red-500'}`}>
                {isPaid ? <CheckCircle size={16} /> : <X size={16} />}
                {isPaid ? 'שולם' : 'לא שולם'}
              </div>
              <div className="h-4 w-[1px] bg-gray-200"></div>
              {canSubmit ? (
                <div className={`flex items-center gap-1.5 font-medium ${isSubmitted ? 'text-blue-600' : 'text-gray-400'}`}>
                  {isSubmitted ? <CheckCircle size={16} /> : <Clock size={16} />}
                  {isSubmitted ? 'הוגש' : 'ממתין להגשה'}
                </div>
              ) : (
                <div className="flex items-center gap-1.5 text-gray-400 text-xs">
                  <Ban size={14} /> מעבר למכסה / פטור
                </div>
              )}
            </div>

            <div className="pt-3 border-t border-gray-50 mt-1">
              {!isPaid ? (
                <div className="text-center text-sm text-red-500 font-medium flex justify-center items-center gap-1 py-1">
                  <Lock size={14} /> יש להסדיר תשלום לפתיחת הקורס
                </div>
              ) : (
                canSubmit ? (
                  <div className="flex flex-col gap-3">
                    
                    {/* --- אפשרות 1: קורס קלאסרום (צהוב) --- */}
                    {isClassroom ? (
                       <div className="bg-yellow-50 border border-yellow-100 rounded-lg p-3">
                          <p className="text-sm text-yellow-900 mb-3 leading-relaxed">
                            <strong>הנחיות לביצוע:</strong><br/>
                            יש להיכנס לקישור, להזין את כתובת המייל שלך, וללחוץ על "הצטרפות". עליך להעלות את המטלות שביצעת בקורס כאן בכפתור העלאה.
                          </p>
                          
                          <div className="flex flex-col gap-2">
                             {/* כפתור כניסה לקלאסרום */}
                             {link && (
                               <a href={link} target="_blank" rel="noreferrer" 
                                  className="w-full py-2 rounded-lg text-sm font-bold flex justify-center items-center gap-2 transition bg-white border border-yellow-300 text-yellow-800 hover:bg-yellow-100">
                                  <ExternalLink size={16} /> מעבר לקלאסרום
                               </a>
                             )}
                             
                             {/* כפתור העלאה */}
                             {!isSubmitted ? (
                               <label className={`w-full flex justify-center items-center gap-2 py-2 rounded-lg text-sm font-bold cursor-pointer transition ${uploadingId === debtId ? 'bg-gray-300' : 'bg-yellow-600 text-white hover:bg-yellow-700 shadow-sm'}`}>
                                  {uploadingId === debtId ? <Loader2 className="animate-spin" size={16} /> : <Upload size={16} />}
                                  {uploadingId === debtId ? "מעלה..." : "העלאת קבצים (ניתן לבחור מספר קבצים)"}
                                  <input type="file" className="hidden" multiple disabled={uploadingId === debtId} onChange={(e) => onUpload(e.target.files, debtId)} />
                               </label>
                             ) : (
                               <div className="w-full bg-green-100 text-green-800 py-2 rounded-lg text-sm font-bold flex justify-center items-center gap-2">
                                  <Check size={16} /> המטלות הוגשו
                               </div>
                             )}
                          </div>
                       </div>
                    ) : (
                       /* --- לא קלאסרום --- */
                       <>
                         {isUrl ? (
                           /* --- אפשרות 2: קורס רגיל עם קישור (כחול) --- */
                           <div className="flex flex-col gap-2">
                              <a href={link} target="_blank" rel="noreferrer" 
                                 className="w-full py-2.5 rounded-lg text-sm font-medium flex justify-center items-center gap-2 border transition bg-gray-50 hover:bg-gray-100 text-gray-700 border-gray-200">
                                 <Download size={18} className="text-gray-500" /> הורדת חומרי למידה
                              </a>

                              {!isSubmitted ? (
                                <label className={`w-full flex justify-center items-center gap-2 py-2.5 rounded-lg text-sm font-bold cursor-pointer shadow-sm transition ${uploadingId === debtId ? 'bg-gray-300' : 'bg-blue-600 text-white hover:bg-blue-700'}`}>
                                  {uploadingId === debtId ? <Loader2 className="animate-spin" size={18} /> : <Upload size={18} />}
                                  {uploadingId === debtId ? "מעלה..." : "העלאת עבודה"}
                                  <input type="file" className="hidden" multiple disabled={uploadingId === debtId} onChange={(e) => onUpload(e.target.files, debtId)} />
                                </label>
                              ) : (
                                <div className="mt-1 w-full bg-green-50/50 border border-green-100 text-green-700 py-2 rounded-lg text-sm font-medium flex justify-center items-center gap-2 cursor-default">
                                  <Check size={16} /> העבודה הוגשה בהצלחה
                                </div>
                              )}
                           </div>
                         ) : (
                           /* --- אפשרות 3: רק טקסט (אין כפתורים) --- */
                           link ? (
                             <div className="bg-blue-50 p-3 rounded text-sm text-blue-900 border border-blue-100 leading-relaxed whitespace-pre-line">
                               <strong>הנחיות לביצוע:</strong><br/>
                               {link}
                             </div>
                           ) : null
                         )}
                       </>
                    )}
                  </div>
                ) : (
                  null
                )
              )}
            </div>
          </div>
        );
      })}
    </div>
  );
};

// --- האפליקציה הראשית ---
function App() {
  const [isFirstInteraction, setIsFirstInteraction] = useState(true);
  const [messages, setMessages] = useState([
    { role: 'bot', text: "ברוכה הבאה למערכת ההשלמות של סמינר 'בית המורה'!\nאני המזכירה הדיגיטלית כאן לשירותך.", icon: 'book' },
    { role: 'bot', text: "כדי שנוכל להתחיל, אנא הקלידי את מספר תעודת הזהות שלך." }
  ]);
  const [input, setInput] = useState('');
  const [loading, setLoading] = useState(false);
  const [studentData, setStudentData] = useState(null);
  const [paymentModal, setPaymentModal] = useState(null);
  const [uploadingId, setUploadingId] = useState(null);

  const messagesEndRef = useRef(null);
  const lastBotMessageRef = useRef(null);

  useEffect(() => {
    if (messages.length === 0) return;
    if (loading) { messagesEndRef.current?.scrollIntoView({ behavior: "smooth" }); return; }
    const lastMsg = messages[messages.length - 1];
    if (lastMsg.role === 'bot') {
      if (lastBotMessageRef.current) { lastBotMessageRef.current.scrollIntoView({ behavior: "smooth", block: "start" }); }
    } else {
      messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
    }
  }, [messages, loading]);

  const handleSend = async () => {
    if (!input.trim()) return;
    if (isFirstInteraction) setIsFirstInteraction(false);
    const userMsg = { role: 'user', text: input };
    setMessages(prev => [...prev, userMsg]);
    setInput('');
    setLoading(true);

    try {
      const currentId = studentData?.studentId || "";
      const response = await axios.post('http://localhost:5219/api/chat/message', { studentId: currentId, userMessage: userMsg.text });
      const data = response.data;

      let finalStudentId = data.studentId || data.StudentId;
      let fName = studentData?.firstName || '';
      let lName = studentData?.lastName || '';
      if (data.data && Array.isArray(data.data) && data.data.length > 0) {
        const first = data.data[0];
        if (!finalStudentId) finalStudentId = first.StudentID || first.studentID;
        fName = first.FirstName || first.firstName || fName;
        lName = first.LastName || first.lastName || lName;
      }

      if (finalStudentId) {
        setStudentData({
          studentId: finalStudentId,
          firstName: fName,
          lastName: lName,
          debts: data.data || []
        });
      }
      setMessages(prev => [...prev, { role: 'bot', text: data.reply, actionType: data.actionType, data: data.data }]);
    } catch (error) {
      console.error(error);
      setMessages(prev => [...prev, { role: 'bot', text: 'שגיאה בתקשורת עם השרת. אנא וודאי שהוא דולק ונסה שנית.' }]);
    } finally {
      setLoading(false);
    }
  };

  const handleKeyPress = (e) => { if (e.key === 'Enter') handleSend(); };

  // --- הפונקציה המעודכנת להעלאת קבצים מרובים ---
  const handleFileUpload = async (files, debtId) => {
    if (!files || files.length === 0) return;
    
    setUploadingId(debtId);
    const formData = new FormData();
    
    // לולאה להוספת כל הקבצים
    for (let i = 0; i < files.length; i++) {
        formData.append('files', files[i]);
    }
    
    formData.append('debtId', debtId);
    // הוספת ת"ז ליתר ביטחון
    if (studentData && studentData.studentId) {
        formData.append('studentId', studentData.studentId);
    }

    try {
      await axios.post('http://localhost:5219/api/submission/upload', formData, { headers: { 'Content-Type': 'multipart/form-data' } });
      alert("הקבצים הועלו בהצלחה!");

      if (studentData) {
        const newDebts = studentData.debts.map(d => {
          const id = getVal(d, 'debtID', 'DebtID');
          return (id === debtId) ? { ...d, IsSubmitted: true, isSubmitted: true } : d;
        });
        setStudentData({ ...studentData, debts: newDebts });
        setMessages(prev => {
          const lastMsg = prev[prev.length - 1];
          if (lastMsg.data) return [...prev.slice(0, -1), { ...lastMsg, data: newDebts }];
          return prev;
        });
      }
    } catch (e) { alert("שגיאה בהעלאה: " + (e.response?.data?.message || "נסה שנית")); } 
    finally { setUploadingId(null); }
  };

  const handleOpenPayment = (debtsToPay, amount) => {
    const student = studentData ? { StudentID: studentData.studentId, FirstName: studentData.firstName, LastName: studentData.lastName } : {};
    setPaymentModal({ amount, student, debts: debtsToPay });
  };

  const onPaymentSuccess = async (transId) => {
    const debtsToPay = paymentModal.debts;
    setPaymentModal(null);
    try {
      await axios.post('http://localhost:5219/api/payment/verify', { TransactionId: transId, StudentId: studentData.studentId });
      const newDebts = studentData.debts.map(d => {
        const id = getVal(d, 'debtID', 'DebtID');
        const wasInPayList = debtsToPay.some(pd => getVal(pd, 'debtID', 'DebtID') === id);
        return wasInPayList ? { ...d, IsPaid: true, isPaid: true } : d;
      });
      setStudentData({ ...studentData, debts: newDebts });
      const hardcodedInstructions = `התשלום התקבל בהצלחה והקורסים נפתחו עבורך כעת.\n\nכדי להשלים את התהליך:\n1. הורידי את חומרי הלימוד.\n2. בצעי את המטלה.\n3. העלי את הקובץ המוכן (ניתן להעלות מס' קבצים) חזרה דרך הכפתור.`;
      setMessages(prev => [...prev, { role: 'bot', text: hardcodedInstructions, data: newDebts, actionType: 'UploadFile' }]);
    } catch (e) { alert("התשלום עבר בנדרים אך נכשלה התקשורת עם השרת לעדכון."); }
  };

  const InputArea = ({ className = "" }) => (
    <div className={`flex gap-2 ${className}`}>
      <input value={input} onChange={e => setInput(e.target.value)} onKeyDown={e => e.key === 'Enter' && handleSend()} spellCheck="false" className="flex-1 bg-white border border-gray-300 rounded-full px-5 py-3 focus:ring-2 focus:ring-[#C77D2B] focus:border-transparent focus:outline-none transition shadow-sm text-gray-700 placeholder-gray-400" placeholder={isFirstInteraction ? "הקלידי כאן מספר ת.ז. (9 ספרות)" : "הקלידי הודעה..."} disabled={loading} autoFocus />
      <button onClick={handleSend} className="bg-gradient-to-b from-[#eebb77] to-[#b06d28] text-white p-3 rounded-full hover:shadow-lg transition shadow-md hover:brightness-105 transform active:scale-95 border border-[#9c5e1f]">
        <Send size={20} className="drop-shadow-sm" />
      </button>
    </div>
  );

  return (
    <div dir="rtl" className="h-screen bg-gray-100 flex items-center justify-center font-sans p-2 sm:p-6">
      <div className="w-full max-w-3xl bg-white h-full max-h-[90vh] rounded-3xl shadow-2xl flex flex-col overflow-hidden border border-gray-200 ring-1 ring-gray-100">
        <header className="bg-gradient-to-r from-blue-700 via-blue-600 to-blue-500 text-white p-4 font-bold flex justify-between items-center shadow-md z-10">
          <div className="flex items-center gap-3">
            <img src="/logo.png" alt="לוגו בית המורה" className="h-14 w-auto object-contain drop-shadow-md bg-white/10 rounded-lg p-1" />
            <h1 className="text-xl font-bold tracking-wide text-white">מזכירה דיגיטלית, כאן בשבילך.</h1>
          </div>
          <Link to="/admin" title="כניסת הנהלה" className="bg-white/20 p-2 rounded-lg hover:bg-white/30 transition backdrop-blur-sm"><Lock size={20} /></Link>
        </header>

        <div className="flex-1 overflow-y-auto p-4 space-y-4 bg-white/50 scrollbar-thin scrollbar-thumb-gray-200 pb-24">
          {messages.map((m, i) => {
            const unpaidList = m.data ? m.data.filter(d => !getVal(d, 'isPaid', 'IsPaid')) : [];
            const totalToPay = unpaidList.length * 1;
            const isLastBotMessage = m.role === 'bot' && i === messages.length - 1;

            return (
              <div key={i} ref={isLastBotMessage ? lastBotMessageRef : null} className={`flex flex-col ${m.role === 'user' ? 'items-end' : 'items-start'} animate-in fade-in slide-in-from-bottom-2 duration-300`}>
                <div className={`p-4 rounded-2xl max-w-[90%] shadow-sm relative ${m.role === 'user' ? 'bg-blue-600 text-white rounded-br-none' : 'bg-gray-100 text-gray-800 rounded-bl-none border border-gray-200'}`}>
                  {m.icon === 'book' && <div className="absolute -top-3 -right-3 bg-white text-blue-600 p-1.5 rounded-full shadow border border-gray-100"><Book size={16} /></div>}
                  <div className="leading-relaxed break-words text-sm sm:text-base" style={{ whiteSpace: 'pre-wrap' }} dangerouslySetInnerHTML={{ __html: m.text }} />
                  
                  {m.actionType === 'ShowDebts' && unpaidList.length > 0 && (
                    <div className="bg-orange-50 border border-orange-200 p-3 rounded-xl flex flex-col gap-2 shadow-sm mt-3">
                      <div><span className="font-bold text-orange-800 text-sm">נדרש תשלום ({unpaidList.length} קורסים)</span><p className="text-xs text-orange-600">סה"כ לתשלום: {totalToPay} ₪</p></div>
                      <button onClick={() => handleOpenPayment(unpaidList, totalToPay)} className="w-full bg-orange-600 hover:bg-orange-700 text-white px-3 py-2 rounded-lg text-sm font-bold shadow flex items-center justify-center gap-2 transition"><CreditCard size={16} /> תשלום אשראי</button>
                    </div>
                  )}

                  {m.data && <DebtsList debts={m.data} onPay={handleOpenPayment} onUpload={handleFileUpload} uploadingId={uploadingId} />}
                </div>
              </div>
            );
          })}
          {loading && <div className="flex justify-start"><div className="bg-gray-50 text-gray-400 px-4 py-3 rounded-2xl rounded-bl-none text-sm flex items-center gap-2 border border-gray-100"><Loader2 className="animate-spin" size={14} /> המזכירה מקלידה...</div></div>}
          {isFirstInteraction && <div className="mt-8 animate-in zoom-in duration-500"><InputArea className="shadow-lg rounded-full" /><p className="text-center text-xs text-gray-400 mt-2">המערכת מאובטחת ודיסקרטית</p></div>}
          <div ref={messagesEndRef} />
        </div>

        {!isFirstInteraction && <div className="p-4 bg-white border-t border-gray-100"><InputArea /></div>}
        {paymentModal && <PaymentIframe totalAmount={paymentModal.amount} student={paymentModal.student} onSuccess={onPaymentSuccess} onClose={() => setPaymentModal(null)} />}
      </div>
    </div>
  );
}

export default App;