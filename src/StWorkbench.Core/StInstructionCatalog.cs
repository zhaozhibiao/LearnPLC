namespace StWorkbench.Core;

public sealed record StInstruction(string Id, string Name, string Category, string Description, string Code, string[] Declarations, bool ReferenceOnly = false, bool Stateful = false)
{
    public string? InstanceNameHint { get; init; }
    public override string ToString() => Name + (ReferenceOnly ? " · 接口示例" : "");
}

// Original portable IEC ST examples. No vendor library or implementation is embedded here.
public static class StInstructionCatalog
{
    private static StInstruction Logic(string id, string name, string expression) => new(id, name, "逻辑与门控", "布尔输入与输出；插入后修改为实际信号。", "{p}Output := " + expression + ";", ["{p}Input : BOOL := FALSE;", "{p}Enable : BOOL := FALSE;", "{p}Output : BOOL;"]);
    private static StInstruction Block(string id, string name, string category, string description, string inputs, params string[] declarations) => new(id, name, category, description, "{p}(" + inputs + ");", ["{p} : " + id + ";", .. declarations]);
    public static IReadOnlyList<StInstruction> All { get; } = Array.AsReadOnly<StInstruction>([
        Logic("AND", "AND 与门 / 使能门控", "{p}Input AND {p}Enable"), Logic("OR", "OR 或门", "{p}Input OR {p}Enable"), Logic("XOR", "XOR 异或", "{p}Input XOR {p}Enable"), Logic("NOT", "NOT 取反", "NOT {p}Input"),
        new("IF", "IF / ELSE 条件分支", "流程与程序段", "条件为 TRUE 执行第一段，否则执行第二段。", "IF {p}Condition THEN\n    (* 条件成立时的程序段 *)\nELSE\n    (* 条件不成立时的程序段 *)\nEND_IF;", ["{p}Condition : BOOL := FALSE;"]),
        new("ELSIF", "IF / ELSIF / ELSE 多条件", "流程与程序段", "按顺序执行首个满足条件的分支。", "IF {p}First THEN\n    (* 第一分支 *)\nELSIF {p}Second THEN\n    (* 第二分支 *)\nELSE\n    (* 默认分支 *)\nEND_IF;", ["{p}First : BOOL := FALSE;", "{p}Second : BOOL := FALSE;"]),
        new("CASE", "CASE 状态机", "流程与程序段", "状态分支模板；替换各步骤中的程序段。", "CASE {p}State OF\n    0: (* 初始化 *)\n        {p}State := 10;\n    10: (* 执行步骤 *)\n        {p}State := 20;\n    20: (* 完成 *)\n        {p}State := 0;\nELSE\n    {p}State := 0;\nEND_CASE;", ["{p}State : INT := 0;"]),
        new("FOR", "FOR 有限循环", "流程与程序段", "固定有限次数循环；避免影响 PLC 扫描周期。", "FOR {p}Index := 1 TO 10 DO\n    (* 循环程序段 *)\nEND_FOR;", ["{p}Index : INT;"]),
        new("WHILE", "WHILE 有界循环", "流程与程序段", "示例带次数上界；修改时保留明确退出条件。", "WHILE {p}Index < 10 DO\n    {p}Index := {p}Index + 1;\nEND_WHILE;", ["{p}Index : INT := 0;"]),
        new("REPEAT", "REPEAT / UNTIL 有界循环", "流程与程序段", "至少执行一次，达到条件后退出。", "REPEAT\n    {p}Index := {p}Index + 1;\nUNTIL {p}Index >= 10\nEND_REPEAT;", ["{p}Index : INT := 0;"]),
        new("SEGMENT", "程序段 / 注释分区", "流程与程序段", "用标准 ST 注释组织输入处理、逻辑和输出。", "(* ===== 输入处理 ===== *)\n\n(* ===== 控制逻辑 ===== *)\n\n(* ===== 输出处理 ===== *)", []),
        new("RETURN", "RETURN 提前返回", "流程与程序段", "满足条件后结束本次 POU 执行。", "IF {p}Exit THEN\n    RETURN;\nEND_IF;", ["{p}Exit : BOOL := FALSE;"]),
        Block("R_TRIG", "R_TRIG 上升沿", "触发与锁存", "Q 在 CLK 从 FALSE 变为 TRUE 时有效一个扫描周期。", "CLK := {p}Signal", "{p}Signal : BOOL := FALSE;"),
        Block("F_TRIG", "F_TRIG 下降沿", "触发与锁存", "Q 在 CLK 从 TRUE 变为 FALSE 时有效一个扫描周期。", "CLK := {p}Signal", "{p}Signal : BOOL := FALSE;"),
        Block("SR", "SR 置位优先锁存", "触发与锁存", "置位优先；输出状态从实例 Q1 读取。", "S1 := {p}Set, R := {p}Reset", "{p}Set : BOOL := FALSE;", "{p}Reset : BOOL := FALSE;"),
        Block("RS", "RS 复位优先锁存", "触发与锁存", "复位优先；输出状态从实例 Q1 读取。", "S := {p}Set, R1 := {p}Reset", "{p}Set : BOOL := FALSE;", "{p}Reset : BOOL := FALSE;"),
        Block("TON", "TON 延时接通", "计时器", "IN 持续为 TRUE 达到 PT 后 Q 为 TRUE；ET 为已计时时间。", "IN := {p}Input, PT := T#1s", "{p}Input : BOOL := FALSE;"),
        Block("TOF", "TOF 延时断开", "计时器", "IN 变为 FALSE 后延时 PT 再使 Q 变为 FALSE。", "IN := {p}Input, PT := T#1s", "{p}Input : BOOL := FALSE;"),
        Block("TP", "TP 定长脉冲", "计时器", "IN 上升沿产生持续 PT 的 Q 脉冲。", "IN := {p}Input, PT := T#100ms", "{p}Input : BOOL := FALSE;"),
        Block("CTU", "CTU 加计数器", "计数器", "CU 上升沿累加，R 复位，CV 为当前计数，Q 表示达到 PV。", "CU := {p}Pulse, R := {p}Reset, PV := 10", "{p}Pulse : BOOL := FALSE;", "{p}Reset : BOOL := FALSE;"),
        Block("CTD", "CTD 减计数器", "计数器", "LD 加载 PV，CD 上升沿递减；CV 为当前计数。", "CD := {p}Pulse, LD := {p}Load, PV := 10", "{p}Pulse : BOOL := FALSE;", "{p}Load : BOOL := FALSE;"),
        Block("CTUD", "CTUD 双向计数器", "计数器", "CU/CD 上升沿增减，R 复位，LD 加载 PV。", "CU := {p}Up, CD := {p}Down, R := {p}Reset, LD := {p}Load, PV := 10", "{p}Up : BOOL := FALSE;", "{p}Down : BOOL := FALSE;", "{p}Reset : BOOL := FALSE;", "{p}Load : BOOL := FALSE;"),
        new("ARITH", "加减乘除 / MOD", "数值与比较", "基础数值运算；示例避免除零。", "{p}Result := ({p}A + {p}B) * 2;\nIF {p}B <> 0 THEN\n    {p}Result := {p}A / {p}B;\n    {p}Remainder := {p}A MOD {p}B;\nEND_IF;", ["{p}A : DINT := 0;", "{p}B : DINT := 1;", "{p}Result : DINT;", "{p}Remainder : DINT;"]),
        new("COMPARE", "比较 / 范围门控", "数值与比较", "大于等于、小于等于与 AND 组成范围判断。", "{p}InRange := ({p}Value >= {p}Low) AND ({p}Value <= {p}High);", ["{p}Value : REAL := 0.0;", "{p}Low : REAL := 0.0;", "{p}High : REAL := 100.0;", "{p}InRange : BOOL;"]),
        new("LIMIT", "LIMIT 限幅", "数值与比较", "限制输出在上下限之间。", "{p}Result := LIMIT(0.0, {p}Value, 100.0);", ["{p}Value : REAL := 0.0;", "{p}Result : REAL;"]),
        new("SEL", "SEL 二选一", "数值与比较", "选择条件为 FALSE 返回第一个值，为 TRUE 返回第二个值。", "{p}Result := SEL({p}Select, 0, 1);", ["{p}Select : BOOL := FALSE;", "{p}Result : INT;"]),
        new("MINMAX", "MIN / MAX 最小最大值", "数值与比较", "取两个输入的最小值和最大值。", "{p}Minimum := MIN({p}A, {p}B);\n{p}Maximum := MAX({p}A, {p}B);", ["{p}A : REAL := 0.0;", "{p}B : REAL := 0.0;", "{p}Minimum : REAL;", "{p}Maximum : REAL;"]),
        new("CONVERT", "类型转换 INT_TO_REAL", "数值与比较", "显式转换整数到浮点数。", "{p}Real := INT_TO_REAL({p}Integer);", ["{p}Integer : INT := 0;", "{p}Real : REAL;"]),
        new("MATH", "ABS / SQRT 数学函数", "数值与比较", "先取绝对值再开方，避免负数输入。", "{p}Result := SQRT(ABS({p}Value));", ["{p}Value : REAL := 0.0;", "{p}Result : REAL;"]),
        new("STRING", "LEN / CONCAT 字符串", "字符串", "字符串拼接与长度；按项目需要调整字符串长度。", "{p}Text := CONCAT('PLC', ' ready');\n{p}Length := LEN({p}Text);", ["{p}Text : STRING;", "{p}Length : INT;"]),
    ]);
}
