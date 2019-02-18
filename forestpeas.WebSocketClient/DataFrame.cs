namespace forestpeas.WebSocketClient
{
    internal sealed class DataFrame
    {
        public bool IsFinBitSet { get; }

        public OpCode OpCode { get; }

        public byte[] Payload { get; }

        public DataFrame(bool isFinBitSet, OpCode opCode, byte[] payload)
        {
            IsFinBitSet = isFinBitSet;
            OpCode = opCode;
            Payload = payload;
        }
    }
}
