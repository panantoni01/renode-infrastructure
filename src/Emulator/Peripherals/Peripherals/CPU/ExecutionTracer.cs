//
// Copyright (c) 2010-2021 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
// 
using System;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Core;

namespace Antmicro.Renode.Peripherals.CPU
{
    public static class ExecutionTracerExtensions
    {
        public static void EnableExecutionTracing(this TranslationCPU @this, string file, ExecutionTracer.Format format)
        {
            var tracer = new ExecutionTracer(@this, file, format);
            // we keep it as external to dispose/flush on quit 
            EmulationManager.Instance.CurrentEmulation.ExternalsManager.AddExternal(tracer, "executionTracer");
            
            tracer.Start();
        }
        
        public static void DisableExecutionTracing(this TranslationCPU @this)
        {
            var em = EmulationManager.Instance.CurrentEmulation.ExternalsManager;
            var tracers = em.GetExternalsOfType<ExecutionTracer>().Where(t => t.AttachedCPU == @this).ToList();
            foreach(var tracer in tracers)
            {
                tracer.Stop();
                em.RemoveExternal(tracer);
            }
        }
    }
    
    public class ExecutionTracer : IDisposable, IExternal
    {
        public ExecutionTracer(TranslationCPU cpu, string file, Format format)
        {
            this.file = file;
            this.format = format;
            this.attachedCPU = cpu;

            try
            {
                // truncate the file
                File.WriteAllText(file, string.Empty);
            }
            catch(Exception e)
            {
                throw new RecoverableException($"There was an error when preparing the execution trace output file {file}: {e.Message}");
            }
            
            attachedCPU.SetHookAtBlockEnd(HandleBlock);
        }

        public void Dispose()
        {
            Stop();
        }

        public void Start()
        {
            blocks = new BlockingCollection<Block>();
            
            underlyingThread = new Thread(WriterThreadBody);
            underlyingThread.IsBackground = true;
            underlyingThread.Name = "Execution tracer worker";
            underlyingThread.Start();
        }

        public void Stop()
        {
            if(underlyingThread == null)
            {
                return;
            }

            this.Log(LogLevel.Info, "Stopping the execution tracer worker and dumping the trace to a file...");
            
            blocks.CompleteAdding();
            underlyingThread.Join();
            underlyingThread = null;

            this.Log(LogLevel.Info, "Execution tracer stopped");
        }
        private void WriterThreadBody()
        {
            while(true)
            {
                try
                {
                    var val = string.Empty;
                    var block = blocks.Take();

                    var pc = block.StartingPC;
                    var counter = 0;

                    while(counter < (int)block.InstructionsCount)
                    {
                        var mem = attachedCPU.Bus.ReadBytes(pc, MaxOpcodeBytes);

                        // TODO: what about flags?
                        if(!attachedCPU.Disassembler.TryDisassembleInstruction(pc, mem, 0, out var result))
                        {
                            val += $"Couldn't disassemble opcode at PC 0x{pc:X}\n";
                            break;
                        }
                        else
                        {
                            switch(format)
                            {
                                case Format.PC:
                                    val += $"0x{pc:X}\n"; 
                                    break;

                                case Format.Opcode:
                                    val += "0x" + result.OpcodeString.ToUpper() + "\n";
                                    break;
                                    
                                case Format.PCAndOpcode:
                                    val += $"0x{pc:X}: 0x{result.OpcodeString.ToUpper()}\n";
                                    break;

                                default:
                                    attachedCPU.Log(LogLevel.Error, "Unsupported format: {0}", format);
                                    break;
                            }
                            
                            pc += (ulong)result.OpcodeSize;
                            counter++;
                        }
                    }
                    
                    // this opens/closes the file for each PC
                    // * it ensures flushing
                    // * it might not be optimal
                    File.AppendAllText(file, val);
                }
                catch(InvalidOperationException)
                {
                    // this happens when the blocking collection is empty and is marked as completed - i.e., we are sure there will be no more elements
                    break;
                }
            }
        }

        private void HandleBlock(ulong pc, uint instructionsInBlock)
        {
            if(instructionsInBlock == 0)
            {
                // ignore
                return;
            }

            try
            {
                blocks.Add(new Block { StartingPC = pc, InstructionsCount = instructionsInBlock });
            }
            catch(InvalidOperationException)
            {
                // this might happen when disposing after `blocks` is marked as closed (not accepting new data)
            }
        }
        
        private Thread underlyingThread;
        private BlockingCollection<Block> blocks;

        private readonly TranslationCPU attachedCPU;
        private readonly string file;
        private readonly Format format;

        private const int MaxOpcodeBytes = 16;
        
        public enum Format
        {
            PC,
            Opcode,
            PCAndOpcode
        }

        private struct Block
        {
            public ulong StartingPC;
            public ulong InstructionsCount;

            public override string ToString()
            {
                return $"[Block: starting at 0x{StartingPC:X} with {InstructionsCount} instructions]";
            }
        }
    }
}
